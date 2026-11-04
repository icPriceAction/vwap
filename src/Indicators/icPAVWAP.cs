// =========================================================================================
// Tool Name:    icPA - VWAP
// Class:        icPAVWAP
// Category:     Indicator
// Version:      1.5.3
// Platform:     NinjaTrader 8.0+
// Author:       Istvan Cebrian
// Brand:        icPriceAction
// Website:      https://icpriceaction.com
// Repository:   https://github.com/icPriceAction/vwap
// YouTube:      https://youtube.com/@icPriceAction
// Support:      https://buymeacoffee.com/icpriceaction
//
// License:      Free for personal trading use. Commercial resale, redistribution,
//               or repackaging is strictly prohibited. See LICENSE in official repository.
//
// Description:  Session, weekly, monthly or custom-start VWAP with up to three time-zone aware
//               anchor resets, five standard deviation bands, previous segment levels and a
//               Z-Score output.
//
// Changelog:
//   v1.5.3              - Initial public release. Cleaned and optimized by icPriceAction.
//   v1.5.2              - Fixed Week/Month resets on daily charts; clearer anchor checks; code comments reviewed.
//   v1.5.1              - Auto Scale defaults to off; the Z-Score plot stays excluded from autoscaling.
//   v1.5.0              - Added VWAP Period (Session/Week/Month/Custom Start), Reset At Session Start,
//                         Custom Start, previous segment VWAP and bands, Z-Score plot, strategy parameters.
//   v1.4.0              - Custom rendering of visible bars only, clean breaks at every segment, Show Band toggles.
//   v1.3.2              - Removed Price Source input; the VWAP always uses typical price (H+L+C)/3.
//   v1.3.1              - Chart bars used by default; optional 'Use Tick Data For Precision' 1-tick engine.
//   v1.3.0              - Secondary-series calculation engine, weighted Welford variance, faster anchor detection.
//   v1.2.0              - Per-bar sums (safe on OnEachTick), anchor timing fix, anchor time zones,
//                         fill colour serialization, equal weighting when no volume data.
//   v1.1.0              - Up to three sequential anchor points that reset the VWAP.
//   v1.0.6              - Added anchor end time parameters (replaced in v1.1.0).
//   v1.0.5              - Compilation fix in first-bar date detection.
//   v1.0.4              - Stateless detection of the first calculation bar.
//   v1.0.3              - State tracking for the first calculation bar.
//   v1.0.2              - Deviation bands now converge on the first bar.
//   v1.0.1              - Anchor time is now applied correctly.
//   v1.0.0              - Initial anchored VWAP implementation.
// =========================================================================================

#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.Gui.SuperDom;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.Core.FloatingPoint;
using NinjaTrader.NinjaScript.DrawingTools;
using NinjaTrader.Gui.Tools;
#endregion

// Enums are declared in the global namespace so that strategies and the Market Analyzer can use them
// as indicator parameters. They carry the class name so they never clash with other scripts.

// Time zone an anchor time is interpreted in.
// Exchange = the instrument's Trading Hours template time zone.
// Platform = the time zone set in Tools > Options > General.
public enum icPAVWAPTimeZone
{
	Exchange,
	Platform,
	NewYork,
	Chicago,
	London,
	Frankfurt,
	Tokyo,
	Sydney
}

// When the VWAP resets.
public enum icPAVWAPPeriod
{
	Session,
	Week,
	Month,
	CustomStart
}

namespace NinjaTrader.NinjaScript.Indicators.icPA
{
	public class icPAVWAP : Indicator
	{
		private const string	NoticeTag			= "icPAVWAPNotice";

		// Weighted running mean and variance (West's weighted version of Welford's algorithm).
		private struct VwapAccumulator
		{
			public double Weight;
			public double Mean;
			public double M2;

			public void Add(double weight, double price)
			{
				if (weight <= 0)
					return;
				Weight		+= weight;
				double delta = price - Mean;
				Mean		+= (weight / Weight) * delta;
				M2			+= weight * delta * (price - Mean);
			}

			public double StdDev()
			{
				return Weight > 0 ? Math.Sqrt(Math.Max(M2 / Weight, 0)) : 0;
			}
		}

		private struct AnchorState
		{
			public bool		Valid;
			public DateTime	Next;		// next anchor at or after the last previous-bar time, in platform time
		}

		// Calculation engine state. 'committed' holds all closed calculation bars of the current segment;
		// the forming bar is kept as 'pending' until the first tick of the next bar commits it.
		private int				calcIndex			= 0;		// BarsInProgress index of the calculation series (0 = chart)
		private VwapAccumulator	committed;
		private bool			hasPending;
		private bool			pendingReset;
		private double			pendingWeight;
		private double			pendingPrice;

		private bool			hasValue;
		private double 			curVWAP;
		private double 			deviation;

		// Segment numbering, used to break lines and fills at each reset when rendering.
		private int				committedSegmentId;
		private int				liveSegmentId;
		private Series<double>	segmentIds;

		// Previous segment levels
		private VwapAccumulator	lastFinishedSegment;			// final totals of the segment before the current one
		private bool			hasPrevious;
		private double			previousVWAP;
		private double			previousDeviation;
		private Series<double>	previousVwapValues;
		private Series<double>	previousDeviationValues;

		// Period state
		private SessionIterator	sessionIterator;
		private bool			isDailyOrHigher;
		private int				sessionCheckBar		= -1;
		private DateTime		currentTradingDay	= DateTime.MinValue;
		private DateTime		previousTradingDay	= DateTime.MinValue;
		private int				customStartBar		= -1;

		private bool			anchorsActive		= true;
		private AnchorState[]	anchorStates		= new AnchorState[3];

		private TimeZoneInfo	platformTimeZone;
		private TimeZoneInfo	anchorTimeZone1;
		private TimeZoneInfo	anchorTimeZone2;
		private TimeZoneInfo	anchorTimeZone3;

		private bool			useEqualWeighting;
		private List<string>	notices				= new List<string>();
		private bool			noticeDrawn;

		// SharpDX device resources, created in OnRenderTargetChanged and disposed there and in Terminated.
		private SharpDX.Direct2D1.Brush[]		fillBrushesDX	= new SharpDX.Direct2D1.Brush[5];
		private SharpDX.Direct2D1.StrokeStyle	previousDashDX;

		// Reused point buffers for rendering (no per-frame list allocations).
		private readonly List<SharpDX.Vector2>	renderPointsA	= new List<SharpDX.Vector2>();
		private readonly List<SharpDX.Vector2>	renderPointsB	= new List<SharpDX.Vector2>();

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description							= "Volume Weighted Average Price with Session, Week, Month or Custom Start periods, up to three time-zone aware anchor resets, five standard deviation bands, previous segment levels and a Z-Score output. Works on any bar type;\r\n"
													+ "Developed by Istvan Cebrian | https://icpriceaction.com\n"
													+ "Support this free tool: https://buymeacoffee.com/icpriceaction";
				Name								= "icPA - VWAP";
				Calculate							= Calculate.OnBarClose;
				IsOverlay							= true;
				IsAutoScale							= false;
				DisplayInDataBox					= true;
				DrawOnPricePanel					= true;
				DrawHorizontalGridLines				= true;
				DrawVerticalGridLines				= true;
				PaintPriceMarkers					= true;
				ScaleJustification					= NinjaTrader.Gui.Chart.ScaleJustification.Right;
				BarsRequiredToPlot					= 0;		// the VWAP is valid from the first bar; no lookback needed

				// Period Defaults
				VwapPeriod							= icPAVWAPPeriod.Session;
				ResetAtSessionStart					= true;
				CustomStartTime						= DateTime.Today;

				// Previous Segment Defaults
				ShowPreviousVWAP					= true;
				ShowPreviousBands					= false;

				// Calculation Defaults
				UseTickData							= false;

				// Anchor Point Defaults
				EnableAnchor1 						= true;
				AnchorTime1 						= DateTime.Parse("09:30", System.Globalization.CultureInfo.InvariantCulture);
				AnchorTimeZone1						= icPAVWAPTimeZone.NewYork;
				EnableAnchor2						= false;
				AnchorTime2 						= DateTime.Parse("08:00", System.Globalization.CultureInfo.InvariantCulture);
				AnchorTimeZone2						= icPAVWAPTimeZone.London;
				EnableAnchor3						= false;
				AnchorTime3 						= DateTime.Parse("02:00", System.Globalization.CultureInfo.InvariantCulture);
				AnchorTimeZone3						= icPAVWAPTimeZone.London;

				ShowBand1							= true;
				ShowBand2							= true;
				ShowBand3							= true;
				ShowBand4							= true;
				ShowBand5							= false;
				SD1 								= 1.28;
				SD2 								= 2.01;
				SD3 								= 2.51;
				SD4 								= 3.1;
				SD5 								= 4.0;

				SD1AreaBrush						= Brushes.CornflowerBlue;
				SD1AreaOpacity						= 6;
				SD2AreaBrush						= Brushes.CornflowerBlue;
				SD2AreaOpacity						= 2;
				SD3AreaBrush						= Brushes.DarkOrange;
				SD3AreaOpacity						= 4;
				SD4AreaBrush						= Brushes.Brown;
				SD4AreaOpacity						= 4;
				SD5AreaBrush						= Brushes.Red;
				SD5AreaOpacity						= 5;

				IsSuspendedWhileInactive			= true;

				// Plot layout (relied on by index throughout): 0 = VWAP, 2*band-1 = band upper,
				// 2*band = band lower (bands 1-5), 11 = previous segment VWAP, 12 = Z-Score.
				AddPlot(new Stroke(Brushes.Black,DashStyleHelper.Dot,1), PlotStyle.Line, "PlotVWAP");
				AddPlot(new Stroke(Brushes.Tan, DashStyleHelper.Dot,1), PlotStyle.Line, "PlotVWAP1U");
				AddPlot(new Stroke(Brushes.Tan, DashStyleHelper.Dot,1), PlotStyle.Line, "PlotVWAP1L");
				AddPlot(Brushes.Orange, "PlotVWAP2U");
				AddPlot(Brushes.Orange, "PlotVWAP2L");
				AddPlot(new Stroke(Brushes.Firebrick,1), PlotStyle.Line, "PlotVWAP3U");
				AddPlot(new Stroke(Brushes.Firebrick,1), PlotStyle.Line, "PlotVWAP3L");
				AddPlot(Brushes.Red, "PlotVWAP4U");
				AddPlot(Brushes.Red, "PlotVWAP4L");
				AddPlot(Brushes.Black, "PlotVWAP5U");
				AddPlot(Brushes.Black, "PlotVWAP5L");
				AddPlot(new Stroke(Brushes.DimGray, DashStyleHelper.Dash, 1), PlotStyle.Line, "PlotPrevVWAP");
				AddPlot(Brushes.Transparent, "PlotZScore");
			}
			else if (State == State.Configure)
			{
				calcIndex		= 0;
				notices.Clear();

				// Fill brushes loaded from templates or workspaces may not be frozen.
				SD1AreaBrush	= FreezeBrush(SD1AreaBrush);
				SD2AreaBrush	= FreezeBrush(SD2AreaBrush);
				SD3AreaBrush	= FreezeBrush(SD3AreaBrush);
				SD4AreaBrush	= FreezeBrush(SD4AreaBrush);
				SD5AreaBrush	= FreezeBrush(SD5AreaBrush);

				BarsPeriodType chartType = BarsPeriod.BarsPeriodType;
				isDailyOrHigher = chartType == BarsPeriodType.Day || chartType == BarsPeriodType.Week
					|| chartType == BarsPeriodType.Month || chartType == BarsPeriodType.Year;

				// Anchors only apply to the Session period, and have no meaning on daily or higher bars.
				anchorsActive = VwapPeriod == icPAVWAPPeriod.Session && !isDailyOrHigher;

				if (isDailyOrHigher)
				{
					if (VwapPeriod == icPAVWAPPeriod.Session)
						notices.Add(Name + ": on daily or higher charts, use VWAP Period Week, Month or Custom Start. Anchors are disabled.");
				}
				else if (UseTickData)
				{
					// Use the chart directly if it already is a 1-tick chart.
					if (!(chartType == BarsPeriodType.Tick && BarsPeriod.Value == 1))
					{
						AddDataSeries(BarsPeriodType.Tick, 1);
						calcIndex = 1;
					}
				}
				else if (chartType == BarsPeriodType.HeikenAshi)
				{
					notices.Add(Name + ": Heiken Ashi bars use synthetic prices. Enable 'Use Tick Data For Precision' for a true VWAP.");
				}
			}
			else if (State == State.DataLoaded)
			{
				platformTimeZone	= NinjaTrader.Core.Globals.GeneralOptions.TimeZoneInfo ?? TimeZoneInfo.Local;
				anchorTimeZone1		= ResolveTimeZone(AnchorTimeZone1);
				anchorTimeZone2		= ResolveTimeZone(AnchorTimeZone2);
				anchorTimeZone3		= ResolveTimeZone(AnchorTimeZone3);
				anchorStates		= new AnchorState[3];

				committed			= new VwapAccumulator();
				hasPending			= false;
				hasValue			= false;
				noticeDrawn			= false;
				committedSegmentId	= 0;
				liveSegmentId		= 0;
				segmentIds			= new Series<double>(this, MaximumBarsLookBack.Infinite);

				lastFinishedSegment		= new VwapAccumulator();
				hasPrevious				= false;
				previousVwapValues		= new Series<double>(this, MaximumBarsLookBack.Infinite);
				previousDeviationValues	= new Series<double>(this, MaximumBarsLookBack.Infinite);

				sessionCheckBar			= -1;
				currentTradingDay		= DateTime.MinValue;
				previousTradingDay		= DateTime.MinValue;
				customStartBar			= -1;

				// Fall back to chart bars if the secondary series loaded no data while the chart did.
				if (calcIndex == 1 && BarsArray[1].Count == 0 && BarsArray[0].Count > 0)
				{
					calcIndex = 0;
					notices.Add(Name + ": no tick history available - calculating on chart bars instead.");
				}

				Bars calcBars		= BarsArray[calcIndex];

				// Used to find each session's trading day for the Week and Month periods.
				sessionIterator		= (!isDailyOrHigher && calcBars != null) ? new SessionIterator(calcBars) : null;

				// If no loaded bar carries any volume (e.g. some forex or CFD feeds), weight every bar equally.
				useEqualWeighting	= false;
				if (calcBars != null && calcBars.Count > 0)
				{
					useEqualWeighting = true;
					for (int i = 0; i < calcBars.Count; i++)
					{
						if (calcBars.GetVolume(i) > 0)
						{
							useEqualWeighting = false;
							break;
						}
					}
				}
				if (useEqualWeighting)
					notices.Add(Name + ": no volume data for this instrument - using equal weighting.");

				// Each notice is printed to the Output window once per load (also shown on the chart).
				for (int n = 0; n < notices.Count; n++)
					Print(notices[n]);
			}
			else if (State == State.Terminated)
			{
				DisposeDxResources();
			}
		}

		protected override void OnBarUpdate()
		{
			if (BarsInProgress == 0 && !noticeDrawn && notices.Count > 0 && ChartControl != null)
			{
				Draw.TextFixed(this, NoticeTag, string.Join(Environment.NewLine, notices), TextPosition.BottomLeft);
				noticeDrawn = true;
			}

			if (BarsInProgress == calcIndex)
				UpdateEngine();

			// Plot only once the engine has a value and the chart series has bars.
			if (!hasValue || CurrentBars[0] < BarsRequiredToPlot || CurrentBars[0] < 0)
				return;

			if (BarsInProgress == 0)
			{
				SetPlotValues();
			}
			else if (BarsInProgress == calcIndex && Times[calcIndex][0] == Times[0][0])
			{
				// NinjaTrader processes the chart bar before a calculation bar with the same timestamp.
				// That calculation bar lies entirely inside the chart bar, so update the chart bar's values.
				SetPlotValues();
			}
		}

		#region Calculation engine
		// Called on every update of the calculation series. Closed calculation bars are committed to
		// 'committed'; the forming bar is kept separately ('pending') and recomputed on every tick,
		// so OnEachTick / OnPriceChange never double-count.
		private void UpdateEngine()
		{
			int i = calcIndex;

			if (IsFirstTickOfBar && hasPending)
			{
				if (pendingReset)
				{
					lastFinishedSegment = committed;
					committed = new VwapAccumulator();
					committedSegmentId++;
				}
				committed.Add(pendingWeight, pendingPrice);
				hasPending = false;
			}

			bool isResetBar;

			if (VwapPeriod == icPAVWAPPeriod.CustomStart)
			{
				// Nothing is calculated or plotted before the start. The first bar closing after the start
				// (the bar containing it) begins the VWAP, the same rule as for anchors.
				if (customStartBar < 0)
				{
					if (Times[i][0] <= CustomStartTime)
					{
						hasValue = false;
						return;
					}
					customStartBar = CurrentBars[i];
				}
				isResetBar = CurrentBars[i] == customStartBar;
			}
			else
			{
				bool firstBar	= CurrentBars[i] == 0;
				// On daily or higher bars every bar is its own session.
				bool newSession	= isDailyOrHigher || BarsArray[i].IsFirstBarOfSession;

				// Track each session's trading day once per bar (stable when the bar updates tick by tick).
				if ((firstBar || newSession) && sessionCheckBar != CurrentBars[i])
				{
					previousTradingDay	= currentTradingDay;
					currentTradingDay	= GetTradingDay(i);
					sessionCheckBar		= CurrentBars[i];
				}
				bool newTradingDay = newSession && previousTradingDay != DateTime.MinValue && currentTradingDay != previousTradingDay;

				switch (VwapPeriod)
				{
					case icPAVWAPPeriod.Week:
						isResetBar = firstBar || (newTradingDay && StartOfWeek(currentTradingDay) != StartOfWeek(previousTradingDay));
						break;
					case icPAVWAPPeriod.Month:
						isResetBar = firstBar || (newTradingDay && (currentTradingDay.Month != previousTradingDay.Month || currentTradingDay.Year != previousTradingDay.Year));
						break;
					default:
						isResetBar = firstBar || (ResetAtSessionStart && newSession);
						// Times[i][1] is only read when this is not the first bar (short-circuit above).
						if (!isResetBar && anchorsActive)
						{
							DateTime previous	= Times[i][1];
							DateTime current	= Times[i][0];
							isResetBar = CrossedAnchor(0, EnableAnchor1, AnchorTime1, anchorTimeZone1, previous, current)
								|| CrossedAnchor(1, EnableAnchor2, AnchorTime2, anchorTimeZone2, previous, current)
								|| CrossedAnchor(2, EnableAnchor3, AnchorTime3, anchorTimeZone3, previous, current);
						}
						break;
				}
			}

			// Typical price (H+L+C)/3; on a 1-tick series this equals the trade price.
			double price = (Highs[i][0] + Lows[i][0] + Closes[i][0]) / 3.0;

			pendingReset	= isResetBar;
			pendingWeight	= useEqualWeighting ? 1.0 : Volumes[i][0];
			pendingPrice	= price;
			hasPending		= true;
			liveSegmentId	= committedSegmentId + (isResetBar ? 1 : 0);

			VwapAccumulator live = isResetBar ? new VwapAccumulator() : committed;
			live.Add(pendingWeight, pendingPrice);

			if (live.Weight > 0)
			{
				curVWAP		= live.Mean;
				deviation	= live.StdDev();
			}
			else
			{
				// No volume yet in this segment: use the bar's price, no bands.
				curVWAP		= price;
				deviation	= 0;
			}

			// Previous segment: on a reset bar that is still forming, 'committed' still holds the segment
			// that just ended; otherwise it is the last finished segment.
			VwapAccumulator previousSegment = isResetBar ? committed : lastFinishedSegment;
			hasPrevious = CurrentBars[i] > 0 && previousSegment.Weight > 0
				&& VwapPeriod != icPAVWAPPeriod.CustomStart;
			if (hasPrevious)
			{
				previousVWAP		= previousSegment.Mean;
				previousDeviation	= previousSegment.StdDev();
			}

			hasValue = true;
		}

		// The trading day a session belongs to (e.g. the Sunday evening ES session belongs to Monday).
		private DateTime GetTradingDay(int barsIndex)
		{
			if (isDailyOrHigher || sessionIterator == null)
				return Times[barsIndex][0].Date;

			try
			{
				sessionIterator.GetNextSession(Times[barsIndex][0], true);
				return sessionIterator.ActualTradingDayExchange.Date;
			}
			catch (Exception)
			{
				return Times[barsIndex][0].Date;
			}
		}

		// Monday of the week containing 'day'.
		private static DateTime StartOfWeek(DateTime day)
		{
			int daysSinceMonday = ((int)day.DayOfWeek + 6) % 7;
			return day.Date.AddDays(-daysSinceMonday);
		}
		#endregion

		#region Anchor helpers
		// True when the calculation bar covering (previous, current] is the first bar that opens at or
		// after the anchor, i.e. previous <= anchor < current (bars are stamped with their close time).
		// The next anchor is cached in platform time and only recomputed once it has been passed, so
		// time zone conversion happens about once per anchor per day, and the result stays stable when
		// the same forming bar is updated tick by tick.
		private bool CrossedAnchor(int slot, bool enabled, DateTime anchorTime, TimeZoneInfo zone, DateTime previous, DateTime current)
		{
			if (!enabled)
				return false;

			if (!anchorStates[slot].Valid || previous > anchorStates[slot].Next)
			{
				anchorStates[slot].Next		= NextAnchorAtOrAfter(previous, anchorTime.TimeOfDay, zone);
				anchorStates[slot].Valid	= true;
			}

			return anchorStates[slot].Next < current;
		}

		// First anchor time at or after 'platformTime', returned in platform time.
		private DateTime NextAnchorAtOrAfter(DateTime platformTime, TimeSpan anchorOfDay, TimeZoneInfo zone)
		{
			DateTime zoneTime	= ToZone(platformTime, zone);
			DateTime candidate	= zoneTime.Date + anchorOfDay;
			if (candidate < zoneTime)
				candidate = candidate.AddDays(1);
			return FromZone(candidate, zone);
		}

		// Converts a platform-time value to the anchor's time zone.
		private DateTime ToZone(DateTime platformTime, TimeZoneInfo zone)
		{
			if (zone == null || platformTimeZone == null || zone.Id == platformTimeZone.Id)
				return platformTime;

			try
			{
				return TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(platformTime, DateTimeKind.Unspecified), platformTimeZone, zone);
			}
			catch (ArgumentException)
			{
				// Time falls in a daylight saving gap in the platform zone; use it unconverted.
				return platformTime;
			}
		}

		// Converts an anchor-zone value back to platform time.
		private DateTime FromZone(DateTime zoneTime, TimeZoneInfo zone)
		{
			if (zone == null || platformTimeZone == null || zone.Id == platformTimeZone.Id)
				return zoneTime;

			DateTime unspecified = DateTime.SpecifyKind(zoneTime, DateTimeKind.Unspecified);
			try
			{
				return TimeZoneInfo.ConvertTime(unspecified, zone, platformTimeZone);
			}
			catch (ArgumentException)
			{
				// Anchor falls in the skipped hour of a spring-forward day; use the first valid time after it.
				try		{ return TimeZoneInfo.ConvertTime(unspecified.AddHours(1), zone, platformTimeZone); }
				catch	(ArgumentException) { return zoneTime; }
			}
		}

		// Maps the time zone option to a Windows time zone; falls back to the platform zone if not found.
		private TimeZoneInfo ResolveTimeZone(icPAVWAPTimeZone option)
		{
			string id = null;

			switch (option)
			{
				case icPAVWAPTimeZone.Exchange:
					if (Bars != null && Bars.TradingHours != null && Bars.TradingHours.TimeZoneInfo != null)
						return Bars.TradingHours.TimeZoneInfo;
					return platformTimeZone;
				case icPAVWAPTimeZone.Platform:		return platformTimeZone;
				case icPAVWAPTimeZone.NewYork:		id = "Eastern Standard Time";		break;
				case icPAVWAPTimeZone.Chicago:		id = "Central Standard Time";		break;
				case icPAVWAPTimeZone.London:		id = "GMT Standard Time";			break;
				case icPAVWAPTimeZone.Frankfurt:	id = "W. Europe Standard Time";		break;
				case icPAVWAPTimeZone.Tokyo:		id = "Tokyo Standard Time";			break;
				case icPAVWAPTimeZone.Sydney:		id = "AUS Eastern Standard Time";	break;
			}

			try
			{
				return TimeZoneInfo.FindSystemTimeZoneById(id);
			}
			catch (Exception)
			{
				Print(Name + ": time zone '" + id + "' not found on this system, using platform time zone instead.");
				return platformTimeZone;
			}
		}
		#endregion

		#region Band helpers
		// Per-band property lookups, so bands can be handled in loops (band = 1..5).
		private bool IsBandShown(int band)
		{
			switch (band)
			{
				case 1:	return ShowBand1;
				case 2:	return ShowBand2;
				case 3:	return ShowBand3;
				case 4:	return ShowBand4;
				default: return ShowBand5;
			}
		}

		private double GetDeviationMultiplier(int band)
		{
			switch (band)
			{
				case 1:	return SD1;
				case 2:	return SD2;
				case 3:	return SD3;
				case 4:	return SD4;
				default: return SD5;
			}
		}

		private Brush GetFillBrush(int band)
		{
			switch (band)
			{
				case 1:	return SD1AreaBrush;
				case 2:	return SD2AreaBrush;
				case 3:	return SD3AreaBrush;
				case 4:	return SD4AreaBrush;
				default: return SD5AreaBrush;
			}
		}

		private int GetFillOpacity(int band)
		{
			switch (band)
			{
				case 1:	return SD1AreaOpacity;
				case 2:	return SD2AreaOpacity;
				case 3:	return SD3AreaOpacity;
				case 4:	return SD4AreaOpacity;
				default: return SD5AreaOpacity;
			}
		}

		// Returns a frozen copy of a WPF brush (or the brush itself if it is already frozen or cannot be).
		private static Brush FreezeBrush(Brush brush)
		{
			if (brush == null || brush.IsFrozen || !brush.CanFreeze)
				return brush;
			Brush copy = brush.Clone();
			copy.Freeze();
			return copy;
		}

		// Sets the VWAP, visible bands, previous segment levels, Z-Score and segment number on the chart's
		// current bar. Safe to call from any BarsInProgress: plot series are synced to the chart's bars.
		private void SetPlotValues()
		{
			PlotVWAP[0]		= curVWAP;
			segmentIds[0]	= liveSegmentId;

			for (int band = 1; band <= 5; band++)
			{
				if (!IsBandShown(band))
					continue;
				double offset			= GetDeviationMultiplier(band) * deviation;
				Values[2 * band - 1][0]	= curVWAP + offset;
				Values[2 * band][0]		= curVWAP - offset;
			}

			// Previous segment levels
			if (hasPrevious)
			{
				previousVwapValues[0]		= previousVWAP;
				previousDeviationValues[0]	= previousDeviation;
				if (ShowPreviousVWAP)
					PlotPrevVWAP[0]			= previousVWAP;
			}
			else
			{
				previousVwapValues.Reset();
				previousDeviationValues.Reset();
				PlotPrevVWAP.Reset();
			}

			// Z-score of the chart's close relative to the VWAP.
			PlotZScore[0] = deviation > 0 ? (Closes[0][0] - curVWAP) / deviation : 0;
		}
		#endregion

		#region Rendering
		// Creates the fill brushes and the dashed stroke style for the current render target.
		// Called by NinjaTrader whenever the render target changes (chart load, resize, etc.).
		public override void OnRenderTargetChanged()
		{
			base.OnRenderTargetChanged();

			DisposeDxResources();

			if (RenderTarget == null)
				return;

			try
			{
				for (int band = 1; band <= 5; band++)
				{
					int opacity	= GetFillOpacity(band);
					Brush fill	= GetFillBrush(band);
					if (opacity <= 0 || fill == null)
						continue;

					SharpDX.Direct2D1.Brush dxBrush = fill.ToDxBrush(RenderTarget);
					dxBrush.Opacity				= Math.Min(opacity, 100) / 100f;
					fillBrushesDX[band - 1]		= dxBrush;
				}

				previousDashDX = new SharpDX.Direct2D1.StrokeStyle(NinjaTrader.Core.Globals.D2DFactory,
					new SharpDX.Direct2D1.StrokeStyleProperties { DashStyle = SharpDX.Direct2D1.DashStyle.Dash });
			}
			catch (Exception)
			{
				DisposeDxResources();
			}
		}

		// Disposes all cached SharpDX resources. Safe to call at any state, including Terminated.
		private void DisposeDxResources()
		{
			if (fillBrushesDX != null)
			{
				for (int n = 0; n < fillBrushesDX.Length; n++)
				{
					if (fillBrushesDX[n] != null)
					{
						fillBrushesDX[n].Dispose();
						fillBrushesDX[n] = null;
					}
				}
			}

			if (previousDashDX != null)
			{
				previousDashDX.Dispose();
				previousDashDX = null;
			}
		}

		// Draws fills and lines for the visible bars only, breaking them at every segment start.
		// base.OnRender is intentionally not called: NinjaTrader's default plot drawing would connect
		// segments and would draw the Z-Score plot.
		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			if (ChartBars == null || RenderTarget == null || segmentIds == null)
				return;

			int from	= Math.Max(ChartBars.FromIndex - 1, 0);
			int to		= Math.Min(ChartBars.ToIndex, Values[0].Count - 1);
			if (to <= from)
				return;

			SharpDX.Direct2D1.AntialiasMode previousMode = RenderTarget.AntialiasMode;
			RenderTarget.AntialiasMode = SharpDX.Direct2D1.AntialiasMode.PerPrimitive;

			// Fills first, so lines are drawn on top. Each band fills from the nearest visible inner band.
			int innerBand = 0;
			for (int band = 1; band <= 5; band++)
			{
				if (!IsBandShown(band))
					continue;

				SharpDX.Direct2D1.Brush dxBrush = fillBrushesDX[band - 1];
				if (dxBrush != null)
				{
					if (innerBand == 0)
						RenderFill(chartControl, chartScale, 2 * band - 1, 2 * band, from, to, dxBrush);
					else
					{
						RenderFill(chartControl, chartScale, 2 * innerBand - 1, 2 * band - 1, from, to, dxBrush);
						RenderFill(chartControl, chartScale, 2 * innerBand, 2 * band, from, to, dxBrush);
					}
				}
				innerBand = band;
			}

			// Previous segment levels (dashed), under the current lines
			if (ShowPreviousBands && previousDashDX != null)
			{
				for (int band = 1; band <= 5; band++)
				{
					if (!IsBandShown(band))
						continue;
					RenderPreviousBand(chartControl, chartScale, band, 1, from, to, previousDashDX);
					RenderPreviousBand(chartControl, chartScale, band, -1, from, to, previousDashDX);
				}
			}
			if (ShowPreviousVWAP)
				RenderLine(chartControl, chartScale, 11, from, to);

			// Lines
			RenderLine(chartControl, chartScale, 0, from, to);
			for (int band = 1; band <= 5; band++)
			{
				if (!IsBandShown(band))
					continue;
				RenderLine(chartControl, chartScale, 2 * band - 1, from, to);
				RenderLine(chartControl, chartScale, 2 * band, from, to);
			}

			RenderTarget.AntialiasMode = previousMode;
		}

		// True when the bar starts a new VWAP segment compared with the bar before it.
		private bool IsSegmentStart(int index)
		{
			if (index <= 0 || !segmentIds.IsValidDataPointAt(index) || !segmentIds.IsValidDataPointAt(index - 1))
				return false;
			return segmentIds.GetValueAt(index) != segmentIds.GetValueAt(index - 1);
		}

		// Draws a plot as polylines using its own colour, width and dash style, one per continuous run.
		private void RenderLine(ChartControl chartControl, ChartScale chartScale, int plotIndex, int from, int to)
		{
			Plot plot = Plots[plotIndex];
			if (plot == null || plot.BrushDX == null)
				return;

			Series<double> series			= Values[plotIndex];
			List<SharpDX.Vector2> points	= renderPointsA;
			points.Clear();

			for (int index = from; index <= to; index++)
			{
				bool valid = series.IsValidDataPointAt(index);

				if (!valid || (points.Count > 0 && IsSegmentStart(index)))
				{
					DrawPolyline(points, plot.BrushDX, plot.Width, plot.StrokeStyle);
					points.Clear();
				}

				if (valid)
					points.Add(new SharpDX.Vector2(chartControl.GetXByBarIndex(ChartBars, index), chartScale.GetYByValue(series.GetValueAt(index))));
			}

			DrawPolyline(points, plot.BrushDX, plot.Width, plot.StrokeStyle);
			points.Clear();
		}

		// Draws one side (side = 1 upper, -1 lower) of a previous segment's band as a dashed line,
		// using the colour and width of the current band's plot.
		private void RenderPreviousBand(ChartControl chartControl, ChartScale chartScale, int band, int side, int from, int to, SharpDX.Direct2D1.StrokeStyle dash)
		{
			Plot plot = Plots[side > 0 ? 2 * band - 1 : 2 * band];
			if (plot == null || plot.BrushDX == null)
				return;

			double multiplier				= GetDeviationMultiplier(band) * side;
			List<SharpDX.Vector2> points	= renderPointsA;
			points.Clear();

			for (int index = from; index <= to; index++)
			{
				bool valid = previousVwapValues.IsValidDataPointAt(index) && previousDeviationValues.IsValidDataPointAt(index);

				if (!valid || (points.Count > 0 && IsSegmentStart(index)))
				{
					DrawPolyline(points, plot.BrushDX, plot.Width, dash);
					points.Clear();
				}

				if (valid)
				{
					double value = previousVwapValues.GetValueAt(index) + multiplier * previousDeviationValues.GetValueAt(index);
					points.Add(new SharpDX.Vector2(chartControl.GetXByBarIndex(ChartBars, index), chartScale.GetYByValue(value)));
				}
			}

			DrawPolyline(points, plot.BrushDX, plot.Width, dash);
			points.Clear();
		}

		// Open path through the points; fewer than two points draws nothing.
		// The PathGeometry depends on the current screen coordinates, so it is built per frame and
		// disposed immediately (device-independent factory resource; brushes and styles are cached).
		private void DrawPolyline(List<SharpDX.Vector2> points, SharpDX.Direct2D1.Brush brush, float width, SharpDX.Direct2D1.StrokeStyle strokeStyle)
		{
			if (points.Count < 2)
				return;

			using (SharpDX.Direct2D1.PathGeometry geometry = new SharpDX.Direct2D1.PathGeometry(NinjaTrader.Core.Globals.D2DFactory))
			{
				using (SharpDX.Direct2D1.GeometrySink sink = geometry.Open())
				{
					sink.BeginFigure(points[0], SharpDX.Direct2D1.FigureBegin.Hollow);
					for (int i = 1; i < points.Count; i++)
						sink.AddLine(points[i]);
					sink.EndFigure(SharpDX.Direct2D1.FigureEnd.Open);
					sink.Close();
				}
				RenderTarget.DrawGeometry(geometry, brush, width, strokeStyle);
			}
		}

		// Fills the area between two plots, one polygon per continuous run within a segment.
		private void RenderFill(ChartControl chartControl, ChartScale chartScale, int plotA, int plotB, int from, int to, SharpDX.Direct2D1.Brush brush)
		{
			Series<double> seriesA			= Values[plotA];
			Series<double> seriesB			= Values[plotB];
			List<SharpDX.Vector2> edgeA		= renderPointsA;
			List<SharpDX.Vector2> edgeB		= renderPointsB;
			edgeA.Clear();
			edgeB.Clear();

			for (int index = from; index <= to; index++)
			{
				bool valid = seriesA.IsValidDataPointAt(index) && seriesB.IsValidDataPointAt(index);

				if (!valid || (edgeA.Count > 0 && IsSegmentStart(index)))
				{
					FillPolygon(edgeA, edgeB, brush);
					edgeA.Clear();
					edgeB.Clear();
				}

				if (valid)
				{
					float x = chartControl.GetXByBarIndex(ChartBars, index);
					edgeA.Add(new SharpDX.Vector2(x, chartScale.GetYByValue(seriesA.GetValueAt(index))));
					edgeB.Add(new SharpDX.Vector2(x, chartScale.GetYByValue(seriesB.GetValueAt(index))));
				}
			}

			FillPolygon(edgeA, edgeB, brush);
			edgeA.Clear();
			edgeB.Clear();
		}

		// Closed polygon: along edge A left to right, back along edge B right to left.
		private void FillPolygon(List<SharpDX.Vector2> edgeA, List<SharpDX.Vector2> edgeB, SharpDX.Direct2D1.Brush brush)
		{
			if (edgeA.Count < 2)
				return;

			using (SharpDX.Direct2D1.PathGeometry geometry = new SharpDX.Direct2D1.PathGeometry(NinjaTrader.Core.Globals.D2DFactory))
			{
				using (SharpDX.Direct2D1.GeometrySink sink = geometry.Open())
				{
					sink.BeginFigure(edgeA[0], SharpDX.Direct2D1.FigureBegin.Filled);
					for (int i = 1; i < edgeA.Count; i++)
						sink.AddLine(edgeA[i]);
					for (int i = edgeB.Count - 1; i >= 0; i--)
						sink.AddLine(edgeB[i]);
					sink.EndFigure(SharpDX.Direct2D1.FigureEnd.Closed);
					sink.Close();
				}
				RenderTarget.FillGeometry(geometry, brush);
			}
		}
		#endregion

		#region Chart scale and label
		// Autoscale on the VWAP, band and previous VWAP plots only, so the Z-Score plot (values around
		// -4..+4) never squashes the price scale.
		public override void OnCalculateMinMax()
		{
			if (ChartBars == null)
				return;

			double min	= double.MaxValue;
			double max	= double.MinValue;
			int from	= Math.Max(ChartBars.FromIndex, 0);
			int to		= Math.Min(ChartBars.ToIndex, Values[0].Count - 1);

			for (int plotIndex = 0; plotIndex <= 11; plotIndex++)
			{
				Series<double> series = Values[plotIndex];
				for (int index = from; index <= to; index++)
				{
					if (!series.IsValidDataPointAt(index))
						continue;
					double value = series.GetValueAt(index);
					if (value < min) min = value;
					if (value > max) max = value;
				}
			}

			if (min <= max)
			{
				MinValue = min;
				MaxValue = max;
			}
		}

		// Keeps the chart label short instead of listing every strategy parameter.
		public override string DisplayName
		{
			get { return Name; }
		}
		#endregion

		#region Properties
		// ---------- 1. General ----------
		// Declaration order of the [NinjaScriptProperty] inputs matches the generated code below.

		[NinjaScriptProperty]
		[Display(Name="VWAP Period", Description="When the VWAP resets. Session: each session start (optional) plus the anchors below. Week / Month: at the first session of each trading week / month. Custom Start: once, at the Custom Start date and time.", Order=0, GroupName="1. General")]
		public icPAVWAPPeriod VwapPeriod { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Reset At Session Start", Description="Session period only. Turn off for a VWAP that resets only at the anchors (e.g. RTH-only from 09:30 New York).", Order=1, GroupName="1. General")]
		public bool ResetAtSessionStart { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Custom Start", Description="Custom Start period only. Date and time the VWAP starts, in the platform time zone (Tools > Options > General).", Order=2, GroupName="1. General")]
		public DateTime CustomStartTime { get; set; }

		// Anchor 1
		[NinjaScriptProperty]
		[Display(Name="Enable Anchor 1", Description="Session period only. Start a new VWAP segment at Anchor Time 1.", Order=3, GroupName="1. General")]
		public bool EnableAnchor1 { get; set; }

		[NinjaScriptProperty]
		[PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")]
		[Display(Name="Anchor Time 1", Description="Time of day the segment starts, in Anchor 1 Time Zone.", Order=4, GroupName="1. General")]
		public DateTime AnchorTime1 { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Anchor 1 Time Zone", Description="Time zone Anchor Time 1 is expressed in. Exchange = the instrument's Trading Hours time zone; Platform = Tools > Options > General time zone.", Order=5, GroupName="1. General")]
		public icPAVWAPTimeZone AnchorTimeZone1 { get; set; }

		// Anchor 2
		[Display(Name="Enable Anchor 2", Description="Session period only. Start a new VWAP segment at Anchor Time 2.", Order=6, GroupName="1. General")]
		public bool EnableAnchor2 { get; set; }

		[PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")]
		[Display(Name="Anchor Time 2", Description="Time of day the segment starts, in Anchor 2 Time Zone.", Order=7, GroupName="1. General")]
		public DateTime AnchorTime2 { get; set; }

		[Display(Name="Anchor 2 Time Zone", Description="Time zone Anchor Time 2 is expressed in. Exchange = the instrument's Trading Hours time zone; Platform = Tools > Options > General time zone.", Order=8, GroupName="1. General")]
		public icPAVWAPTimeZone AnchorTimeZone2 { get; set; }

		// Anchor 3
		[Display(Name="Enable Anchor 3", Description="Session period only. Start a new VWAP segment at Anchor Time 3.", Order=9, GroupName="1. General")]
		public bool EnableAnchor3 { get; set; }

		[PropertyEditor("NinjaTrader.Gui.Tools.TimeEditorKey")]
		[Display(Name="Anchor Time 3", Description="Time of day the segment starts, in Anchor 3 Time Zone.", Order=10, GroupName="1. General")]
		public DateTime AnchorTime3 { get; set; }

		[Display(Name="Anchor 3 Time Zone", Description="Time zone Anchor Time 3 is expressed in. Exchange = the instrument's Trading Hours time zone; Platform = Tools > Options > General time zone.", Order=11, GroupName="1. General")]
		public icPAVWAPTimeZone AnchorTimeZone3 { get; set; }

		[NinjaScriptProperty]
		[Display(Name="Use Tick Data For Precision", Description="Calculate on 1-tick data instead of the chart's bars: exact trade prices, anchors precise to the tick, and a true VWAP on Heiken Ashi charts. Loads full tick history (slower on long lookbacks). Tick Replay is not required. A strategy calling this indicator with this on must add AddDataSeries(BarsPeriodType.Tick, 1) itself.", Order=12, GroupName="1. General")]
		public bool UseTickData { get; set; }

		// ---------- 2. Visuals ----------

		[Display(Name="Show Previous VWAP", Description="Carry the previous segment's final VWAP forward as a dashed line across the current segment. Style it under Plots > PlotPrevVWAP.", Order=20, GroupName="2. Visuals")]
		public bool ShowPreviousVWAP { get; set; }

		[Display(Name="Show Previous Bands", Description="Also carry the previous segment's final bands forward, as dashed lines in each band's colour.", Order=21, GroupName="2. Visuals")]
		public bool ShowPreviousBands { get; set; }

		// Band 1
		[Display(Name="Show Band 1", Description="Plot band 1 and its fill.", Order=22, GroupName="2. Visuals")]
		public bool ShowBand1 { get; set; }

		[XmlIgnore]
		[Display(Name="Band 1 Fill Color", Description="Fill colour between the VWAP and band 1.", Order=23, GroupName="2. Visuals")]
		public Brush SD1AreaBrush { get; set; }

		[Browsable(false)]
		public string SD1AreaBrushSerializable
		{
			get { return Serialize.BrushToString(SD1AreaBrush); }
			set { SD1AreaBrush = Serialize.StringToBrush(value); }
		}

		[Range(0, 100)]
		[Display(Name="Band 1 Fill Opacity", Description="Fill opacity, 0-100 (0 = no fill).", Order=24, GroupName="2. Visuals")]
		public int SD1AreaOpacity { get; set; }

		// Band 2
		[Display(Name="Show Band 2", Description="Plot band 2 and its fill.", Order=25, GroupName="2. Visuals")]
		public bool ShowBand2 { get; set; }

		[XmlIgnore]
		[Display(Name="Band 2 Fill Color", Description="Fill colour between band 2 and the nearest visible inner band.", Order=26, GroupName="2. Visuals")]
		public Brush SD2AreaBrush { get; set; }

		[Browsable(false)]
		public string SD2AreaBrushSerializable
		{
			get { return Serialize.BrushToString(SD2AreaBrush); }
			set { SD2AreaBrush = Serialize.StringToBrush(value); }
		}

		[Range(0, 100)]
		[Display(Name="Band 2 Fill Opacity", Description="Fill opacity, 0-100 (0 = no fill).", Order=27, GroupName="2. Visuals")]
		public int SD2AreaOpacity { get; set; }

		// Band 3
		[Display(Name="Show Band 3", Description="Plot band 3 and its fill.", Order=28, GroupName="2. Visuals")]
		public bool ShowBand3 { get; set; }

		[XmlIgnore]
		[Display(Name="Band 3 Fill Color", Description="Fill colour between band 3 and the nearest visible inner band.", Order=29, GroupName="2. Visuals")]
		public Brush SD3AreaBrush { get; set; }

		[Browsable(false)]
		public string SD3AreaBrushSerializable
		{
			get { return Serialize.BrushToString(SD3AreaBrush); }
			set { SD3AreaBrush = Serialize.StringToBrush(value); }
		}

		[Range(0, 100)]
		[Display(Name="Band 3 Fill Opacity", Description="Fill opacity, 0-100 (0 = no fill).", Order=30, GroupName="2. Visuals")]
		public int SD3AreaOpacity { get; set; }

		// Band 4
		[Display(Name="Show Band 4", Description="Plot band 4 and its fill.", Order=31, GroupName="2. Visuals")]
		public bool ShowBand4 { get; set; }

		[XmlIgnore]
		[Display(Name="Band 4 Fill Color", Description="Fill colour between band 4 and the nearest visible inner band.", Order=32, GroupName="2. Visuals")]
		public Brush SD4AreaBrush { get; set; }

		[Browsable(false)]
		public string SD4AreaBrushSerializable
		{
			get { return Serialize.BrushToString(SD4AreaBrush); }
			set { SD4AreaBrush = Serialize.StringToBrush(value); }
		}

		[Range(0, 100)]
		[Display(Name="Band 4 Fill Opacity", Description="Fill opacity, 0-100 (0 = no fill).", Order=33, GroupName="2. Visuals")]
		public int SD4AreaOpacity { get; set; }

		// Band 5
		[Display(Name="Show Band 5", Description="Plot band 5 and its fill.", Order=34, GroupName="2. Visuals")]
		public bool ShowBand5 { get; set; }

		[XmlIgnore]
		[Display(Name="Band 5 Fill Color", Description="Fill colour between band 5 and the nearest visible inner band.", Order=35, GroupName="2. Visuals")]
		public Brush SD5AreaBrush { get; set; }

		[Browsable(false)]
		public string SD5AreaBrushSerializable
		{
			get { return Serialize.BrushToString(SD5AreaBrush); }
			set { SD5AreaBrush = Serialize.StringToBrush(value); }
		}

		[Range(0, 100)]
		[Display(Name="Band 5 Fill Opacity", Description="Fill opacity, 0-100 (0 = no fill).", Order=36, GroupName="2. Visuals")]
		public int SD5AreaOpacity { get; set; }

		// ---------- 3. Offsets & Thresholds ----------

		[Range(0, 10)]
		[Display(Name="Band 1 Deviation", Description="Band 1 distance from VWAP, in standard deviations.", Order=40, GroupName="3. Offsets & Thresholds")]
		public double SD1 { get; set; }

		[Range(0, 10)]
		[Display(Name="Band 2 Deviation", Description="Band 2 distance from VWAP, in standard deviations.", Order=41, GroupName="3. Offsets & Thresholds")]
		public double SD2 { get; set; }

		[Range(0, 10)]
		[Display(Name="Band 3 Deviation", Description="Band 3 distance from VWAP, in standard deviations.", Order=42, GroupName="3. Offsets & Thresholds")]
		public double SD3 { get; set; }

		[Range(0, 10)]
		[Display(Name="Band 4 Deviation", Description="Band 4 distance from VWAP, in standard deviations.", Order=43, GroupName="3. Offsets & Thresholds")]
		public double SD4 { get; set; }

		[Range(0, 10)]
		[Display(Name="Band 5 Deviation", Description="Band 5 distance from VWAP, in standard deviations.", Order=44, GroupName="3. Offsets & Thresholds")]
		public double SD5 { get; set; }

		// ---------- Plots (exposed series) ----------
		[Browsable(false)]
		[XmlIgnore]
		public Series<double> PlotVWAP { get { return Values[0]; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> PlotVWAP1U { get { return Values[1]; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> PlotVWAP1L { get { return Values[2]; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> PlotVWAP2U { get { return Values[3]; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> PlotVWAP2L { get { return Values[4]; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> PlotVWAP3U { get { return Values[5]; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> PlotVWAP3L { get { return Values[6]; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> PlotVWAP4U { get { return Values[7]; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> PlotVWAP4L { get { return Values[8]; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> PlotVWAP5U { get { return Values[9]; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> PlotVWAP5L { get { return Values[10]; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> PlotPrevVWAP { get { return Values[11]; } }

		[Browsable(false)]
		[XmlIgnore]
		public Series<double> PlotZScore { get { return Values[12]; } }
		#endregion
	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private icPA.icPAVWAP[] cacheicPAVWAP;
		public icPA.icPAVWAP icPAVWAP(icPAVWAPPeriod vwapPeriod, bool resetAtSessionStart, DateTime customStartTime, bool enableAnchor1, DateTime anchorTime1, icPAVWAPTimeZone anchorTimeZone1, bool useTickData)
		{
			return icPAVWAP(Input, vwapPeriod, resetAtSessionStart, customStartTime, enableAnchor1, anchorTime1, anchorTimeZone1, useTickData);
		}

		public icPA.icPAVWAP icPAVWAP(ISeries<double> input, icPAVWAPPeriod vwapPeriod, bool resetAtSessionStart, DateTime customStartTime, bool enableAnchor1, DateTime anchorTime1, icPAVWAPTimeZone anchorTimeZone1, bool useTickData)
		{
			if (cacheicPAVWAP != null)
				for (int idx = 0; idx < cacheicPAVWAP.Length; idx++)
					if (cacheicPAVWAP[idx] != null && cacheicPAVWAP[idx].VwapPeriod == vwapPeriod && cacheicPAVWAP[idx].ResetAtSessionStart == resetAtSessionStart && cacheicPAVWAP[idx].CustomStartTime == customStartTime && cacheicPAVWAP[idx].EnableAnchor1 == enableAnchor1 && cacheicPAVWAP[idx].AnchorTime1 == anchorTime1 && cacheicPAVWAP[idx].AnchorTimeZone1 == anchorTimeZone1 && cacheicPAVWAP[idx].UseTickData == useTickData && cacheicPAVWAP[idx].EqualsInput(input))
						return cacheicPAVWAP[idx];
			return CacheIndicator<icPA.icPAVWAP>(new icPA.icPAVWAP(){ VwapPeriod = vwapPeriod, ResetAtSessionStart = resetAtSessionStart, CustomStartTime = customStartTime, EnableAnchor1 = enableAnchor1, AnchorTime1 = anchorTime1, AnchorTimeZone1 = anchorTimeZone1, UseTickData = useTickData }, input, ref cacheicPAVWAP);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.icPA.icPAVWAP icPAVWAP(icPAVWAPPeriod vwapPeriod, bool resetAtSessionStart, DateTime customStartTime, bool enableAnchor1, DateTime anchorTime1, icPAVWAPTimeZone anchorTimeZone1, bool useTickData)
		{
			return indicator.icPAVWAP(Input, vwapPeriod, resetAtSessionStart, customStartTime, enableAnchor1, anchorTime1, anchorTimeZone1, useTickData);
		}

		public Indicators.icPA.icPAVWAP icPAVWAP(ISeries<double> input , icPAVWAPPeriod vwapPeriod, bool resetAtSessionStart, DateTime customStartTime, bool enableAnchor1, DateTime anchorTime1, icPAVWAPTimeZone anchorTimeZone1, bool useTickData)
		{
			return indicator.icPAVWAP(input, vwapPeriod, resetAtSessionStart, customStartTime, enableAnchor1, anchorTime1, anchorTimeZone1, useTickData);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.icPA.icPAVWAP icPAVWAP(icPAVWAPPeriod vwapPeriod, bool resetAtSessionStart, DateTime customStartTime, bool enableAnchor1, DateTime anchorTime1, icPAVWAPTimeZone anchorTimeZone1, bool useTickData)
		{
			return indicator.icPAVWAP(Input, vwapPeriod, resetAtSessionStart, customStartTime, enableAnchor1, anchorTime1, anchorTimeZone1, useTickData);
		}

		public Indicators.icPA.icPAVWAP icPAVWAP(ISeries<double> input , icPAVWAPPeriod vwapPeriod, bool resetAtSessionStart, DateTime customStartTime, bool enableAnchor1, DateTime anchorTime1, icPAVWAPTimeZone anchorTimeZone1, bool useTickData)
		{
			return indicator.icPAVWAP(input, vwapPeriod, resetAtSessionStart, customStartTime, enableAnchor1, anchorTime1, anchorTimeZone1, useTickData);
		}
	}
}

#endregion
