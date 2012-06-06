//
//----------------------------------------------------------------------------
//
// WUnderground
//
// Copyright (C) 2011 by Robert Paauwe
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
//----------------------------------------------------------------------------
//
// A driver that queries the Weather Underground for weather data. Pulls 
// current condition data at a user defined interval from the Weather
// Underground main feed.  Forecast data is queried
// twice a day for the location of the defined weather station.
//
// The data will be in english or metric units as defined by the Units
// configuration option.
// 
// The properties exposed are:
//
//	Credit                  Provides credit to Weather Underground for data feed
//	DewPoint                Current dewpoint
//	HeatIndex               Current heat index
//	Humidity                Current humidity
//	Location                Location of weather station
//	Precipitation           Precipitation for today
//	PrecipitationChance     Chance of Precipitation for today
//	BarometricPressure      Barometric pressure
//	BarometricTrend         Barometric pressure trend up/down/steady
//	BarometricUnits         Barometric pressure units
//	SolarRadiation          Solar Radiation if reported by station
//	Temperature             Current temperature
//	UVIndex                 Current UV Index
//	WindDirectionDegrees    Wind direction in degrees (numeric)
//	WindDirectionText       Wind direction (N, NW, S, etc.)
//	WindGustSpeed           Highest wind speed reported
//	WindSpeed               Current wind speed
//	Windchill               Current windchill 
//  ApparentTemperature     Calculated apparent temperature (feels like)
//	LastUpdate              Last time/date that the data was reported

//	Dates                   Array of dates for forecast data
//	WeekDayTexts            Array of day names for forecast data
//	Highs                   Array of expected high temperatures
//	Lows                    Array of expected low temperatures
//	Conditions              Array of expected conditions (text)
//  DayIconIDs              Array of icon id numbers for forecast conditions
//  NightIconIDs            Array of icon id numbers for forecast conditions
//	DayDescriptions         Array of daily forecast text
//	NightDescriptions       Array of nightly forecast text
//	NightConditions         Array of nightly short condition text
//  PrecipitationChanceDay  Array of daily chance of precipitation 
//	ConditionIconUrls       Array of URL's pointing to the condition icon
//  ---  URL's to the various icon sets supported by Weather Underground
//  SmileyConditionIconUrls
//  GenericConditionIconUrls
//  OldSchoolConditionIconUrls
//  CartoonConditionIconUrls
//  MobileConditionIconUrls
//  SimpleConditionIconUrls
//  ContemporaryConditionIconUrls
//  HelenConditionIconUrls
//
// The methods exposed are:
//   none
//
//

using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using CodecoreTechnologies.Elve.DriverFramework;
using CodecoreTechnologies.Elve.DriverFramework.Communication;
using CodecoreTechnologies.Elve.DriverFramework.DeviceSettingEditors;
using CodecoreTechnologies.Elve.DriverFramework.DriverInterfaces;
using CodecoreTechnologies.Elve.DriverFramework.Scripting;
using System.Threading;
using System.Timers;
using System.IO;
using System.Xml;
using System.Net;
using System.Net.Sockets;
using System.Linq;

namespace WUnderground {

	[Driver(
			"Weather Underground",
			"This driver queries weather data from the Weather Underground. " +
			"It requires a query location string to identify the location " +
			"for the current conditions and forecast.\r\n\r\n" +
			"The location string can be one of the following:\r\n" +
			"  STATE/CITY_NAME     ex: CA/San_Francisco\r\n" +
			"  US Zip code         ex: 60290\r\n" +
			"  Country/city_name   ex: Austrailia/Sydney\r\n" +
			"  latitude,longitude  ex: 37.8,-122.4\r\n" +
			"  Airport code        ex: KJFK\r\n" +
			"  pws:PWS Id          ex: pws:KCASANFR70\r\n" +
			"  autoip              ex: autoip\r\n",
			"Robert Paauwe",
			"Weather",
			"",
			"weather",
			DriverCommunicationPort.Network,
			DriverMultipleInstances.MultiplePerDriverService,
			1, // Major version
			1, // Minor version
			DriverReleaseStages.Production,
			"Weather Underground, Inc.",
			"http://www.wunderground.com/",
			null
			)]
	public class WeatherUndergroundDriver : Driver, IWeatherDriver {
		private string key = "XXXXXXXXXXXXXXXX";  // Set via driver Settings
		private System.Timers.Timer m_poll_timer;
		private string m_location;
		private string m_station_location = "";
		private int m_device_poll;
		private bool m_metric;
		private WeatherData m_weather;
		private Forecast[] m_forecasts = new Forecast[8]; // Currently up to 7 days of info.

		//
		// Driver user configuration settings
		//
		// Weather station identification string
		// Units (english or metric)
		// Polling interval
		//
		[DriverSettingAttribute("Location",
				"The weather station identifier to query. Search for station ID's at " +
				"http://www.wunderground.com/weatherstation/index.asp",
				null, true)]
		public string StationIDSetting {
			set {
				m_location = value;
			}
		}

		[DriverSettingAttribute("Access Key",
				"The WUI Application Programming Interface Key that provides authorized access to the " +
				"WUI API. See http://www.wunderground.com/weather/api/d/terms.html for the terms of " +
				"use.",
				null, true)]
		public string APIKeySetting {
			set {
				key = value;
			}
		}

		[DriverSettingAttribute("Units",
				"Use English or Metric units.",
				new string[] { "English", "Metric" }, "English", true)]
		public string UnitsSetting {
			set {
				if (value == "Metric") {
					m_metric = true;
				} else {
					m_metric = false;
				}
			}
		}

		[DriverSettingAttribute("Polling Interval",
				"The interval used to query current condition information from " +
				"the Weather Underground server, in seconds.",
				180, 3600, "600", true)]
		public int PollIntervalSetting {
			set {
				m_device_poll = value;
			}
		}

		//
		// ----------------------------------------------------------------
		//  Driver start and stop methods
		// ----------------------------------------------------------------
		//
		public override bool StartDriver(
				Dictionary<string, byte[]> configFileData) {
			DirectoryInfo localpath = new DirectoryInfo(LocalDeviceDataDirectoryPath);

			//Logger.Info(DriverDisplayNameInternal + " Driver version " + 
			Logger.Info("Weather Underground Driver version " +
				System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString() +
				" starting.");

			// Allocate data classes
			for (int i = 0; i < m_forecasts.Length; i++) {
				m_forecasts[i] = new Forecast(m_metric);
			}
			m_weather = new WeatherData(m_metric);

			// Determine the URL(s) that will be used to query for the information.
			// The format for the URLs is:
			// http://api.wunderground.com/api/KEY/FEATURE/[FEATURE…]/q/QUERY.FORMAT
			//
			// For now, I want to use the xml format
			//
			// the QUERY value can be one of:
			// the location for which you want weather information. Examples:
			//  CA/San_Francisco
			//  60290 (U.S. zip code)
			//  Australia/Sydney
			//  37.8,-122.4 (latitude,longitude)
			//  KJFK (airport code)
			//  pws:KCASANFR70 (PWS id)
			//  autoip (AutoIP address location)
			//  autoip.json?geo_ip=38.102.136.138 (Specific IP address location)

			
			// Attempt connection to the Weather Underground server and pull current values
			if (ReadWeatherData(m_location)) {
				ApparentTemp();
			}

			// Start a timer to pull data at polling frequency.
			m_poll_timer = new System.Timers.Timer();
			m_poll_timer.Elapsed += new ElapsedEventHandler(PollWUnderground);
			m_poll_timer.Interval = m_device_poll * 1000;
			m_poll_timer.Enabled = true;

			return true;
		}

		public override void StopDriver() {
			Logger.Info("Weather Underground Driver finished.");
			m_poll_timer.Enabled = false;
			m_poll_timer.Dispose();
		}


		//
		// ----------------------------------------------------------------
		//  Driver public properties
		// ----------------------------------------------------------------
		//

		//
		// The following properties represent the current weather conditions. For
		// the most part, they get updated at the polling interval.  See
		// ReadWeatherData() for the URL that is used and XML parsing of the returned
		// data.
		//
        [ScriptObjectPropertyAttribute("Location", "Gets the location for the weather.",
			"the {NAME} weather location", null)]
        public ScriptString Location {
            get {
                // return text description of the weather location, such as city, state, etc.
				return new ScriptString(m_weather.Location);
            }
        }

        [ScriptObjectPropertyAttribute("Temperature", "Gets the current temperature.",
			"the {NAME} temperature", null)]
        [SupportsDriverPropertyBinding]
        public ScriptNumber Temperature {
            get {
				return new ScriptNumber(m_weather.GetTemperature());
            }
        }

        [ScriptObjectPropertyAttribute("Apparent Temperature", "Gets the apparent temperature.",
			"the {NAME} apparent temperature", null)]
        [SupportsDriverPropertyBinding]
        public ScriptNumber ApparentTemperature {
            get {
				return new ScriptNumber(m_weather.GetApparentTemperature());
            }
        }

        [ScriptObjectPropertyAttribute("Current Condition", "Gets the current condition.",
			"the {NAME} current condition text", null)]
        [SupportsDriverPropertyBinding]
        public ScriptString CurrentCondition {
            get {
				return new ScriptString(m_forecasts[0].Condition);
            }
        }

        [ScriptObjectPropertyAttribute("Wind Speed", "Gets the windspeed in miles/hour.",
			"the {NAME} windspeed in miles/hour", null)]
        [SupportsDriverPropertyBinding]
        public ScriptNumber WindSpeed {
            get {
				return new ScriptNumber(m_weather.WindSpeed);
            }
        }

        [ScriptObjectPropertyAttribute("Wind Gust Speed", "Gets the gust windspeed in miles/hour.",
			"the {NAME} gust windspeed in miles/hour", null)]
        [SupportsDriverPropertyBinding]
        public ScriptNumber WindGustSpeed {
            get {
				return new ScriptNumber(m_weather.WindGust);
            }
        }

        [ScriptObjectPropertyAttribute("Wind Direction Degrees", "Gets the wind direction in degrees.",
			"the {NAME} wind direction in degrees", null)]
        [SupportsDriverPropertyBinding]
        public ScriptNumber WindDirectionDegrees {
            get {
				return new ScriptNumber(m_weather.WindDegrees);
            }
        }

        [ScriptObjectPropertyAttribute("Wind Direction Text", "Gets the wind direction as text. Ex: NW or E.",
			"the {NAME} wind direction abbreviation", null)]
        [SupportsDriverPropertyBinding]
        public ScriptString WindDirectionText {
            get {
				return new ScriptString(m_weather.WindDirection);
            }
        }

        [ScriptObjectPropertyAttribute("Humidity", "Gets the percent relative humidity.",
			"the {NAME} percent relative humidity", null)]
        [SupportsDriverPropertyBinding]
        public ScriptNumber Humidity {
            get {
				return new ScriptNumber(m_weather.Humidity);
            }
        }


        [ScriptObjectPropertyAttribute("Dew Point", "Gets the dew point temperature.",
			"the {NAME} dewpoint temperature", null)]
        [SupportsDriverPropertyBinding]
        public ScriptNumber DewPoint {
            get {
				return new ScriptNumber(m_weather.GetDewpoint());
            }
        }

        [ScriptObjectPropertyAttribute("Barometric Pressure", "Gets the barometric pressure.",
			"the {NAME} barometric pressure", null)]
        [SupportsDriverPropertyBinding]
        public ScriptNumber BarometricPressure {
            get {
				return new ScriptNumber(m_weather.GetPressure);
            }
        }

		[ScriptObjectPropertyAttribute("Barometric Pressure Trend", "Gets the barometric pressure trend.",
			"the {NAME} barometric pressure trend", null)]
		[SupportsDriverPropertyBinding]
		public ScriptString BarometricTrend {
			get {
				return new ScriptString(m_weather.PressureTrending);
			}
		}

		[ScriptObjectPropertyAttribute("Barometric Pressure Units", "Gets the barometric pressure units of measure.",
			"the {NAME} barometric pressure units of measure", null)]
		[SupportsDriverPropertyBinding]
		public ScriptString BarometricUnits {
			get {
				return new ScriptString(m_weather.PressureUnits);
			}
		}

        [ScriptObjectPropertyAttribute("Heat Index", "Gets the heat index temperature.",
			"the {NAME} heat index", null)]
        [SupportsDriverPropertyBinding]
        public ScriptNumber HeatIndex {
            get {
				return new ScriptNumber(m_weather.GetHeatIndex());
            }
        }

        [ScriptObjectPropertyAttribute("Windchill", "Gets the windchill temperature.",
			"the {NAME} windchill", null)]
        [SupportsDriverPropertyBinding]
        public ScriptNumber Windchill {
            get {
				return new ScriptNumber(m_weather.GetWindChill());
            }
        }

        [ScriptObjectPropertyAttribute("Precipitation", "Gets the daily amount of precipitation.",
			"the {NAME} precipitation", null)]
        [SupportsDriverPropertyBinding]
        public ScriptNumber Precipitation {
            get {
				return new ScriptNumber(m_weather.GetPrecipitation());
            }
        }

        [ScriptObjectPropertyAttribute("Solar Radiation", "Gets the solar radiation.",
			"the {NAME} solar radiation", null)]
        [SupportsDriverPropertyBinding]
        public ScriptNumber SolarRadiation {
            get {
				return new ScriptNumber(m_weather.SolarRadiation);
            }
        }

        [ScriptObjectPropertyAttribute("UV Index", "Gets the UV Index.",
			"the {NAME} UV index", null)]
        [SupportsDriverPropertyBinding]
        public ScriptNumber UVIndex {
            get {
				return new ScriptNumber(m_weather.UV);
            }
        }

		[ScriptObjectPropertyAttribute("Credit", "Provide credit for using Weather Underground data feeds.",
			"the {NAME} data feed credit", null)]
		[SupportsDriverPropertyBinding]
		public ScriptString Credit {
			get {
				return new ScriptString("Data source provided by " + m_weather.Credit);
			}
		}

		[ScriptObjectPropertyAttribute("Credit URL", "Provide a link to the Weather Underground site.",
			"the {NAME} URL", null)]
		[SupportsDriverPropertyBinding]
		public ScriptString CreditURL {
			get {
				return new ScriptString(m_weather.CreditURL);
			}
		}

		[ScriptObjectPropertyAttribute("Visibility", "Get the current visibility.",
			"the {NAME} visibility", null)]
		[SupportsDriverPropertyBinding]
		public ScriptNumber Visibility {
			get {
				return new ScriptNumber(m_weather.GetVisibility);
			}
		}

		[ScriptObjectPropertyAttribute("Elevation", "Get the elevation of at the reporting weather station.",
			"the {NAME} elevation", null)]
		[SupportsDriverPropertyBinding]
		public ScriptString Elevation {
			get {
				return new ScriptString(m_weather.Elevation);
			}
		}

		[ScriptObjectPropertyAttribute("Last Update", "Date and Time that last update was recieved.",
			"the {NAME} data feed observation time", null)]
		[SupportsDriverPropertyBinding]
		public ScriptString LastUpdate {
			get {
				return new ScriptString(m_weather.LastUpdate);
			}
		}

		//
		// The following properties represent Forecasted weather data. This data will get
		// updated hourly.  See ReadWeatherForecast() for the URL and XML parsing.
		//
		// Weather Underground provides 6 days worth of forecast data. Day 0 is the 
		// current day.
		// 

        [ScriptObjectPropertyAttribute("Highs", "Gets an array of daily maximum temperatures.",
			"the {NAME} maximum temperature for day {INDEX|0}", null)]
        [SupportsDriverPropertyBinding]
        public IScriptArray Highs {
            get {
				return new ScriptArrayMarshalByValue(m_forecasts.Select(f => f.GetHigh()), 0);
            }
        }

        [ScriptObjectPropertyAttribute("Lows", "Gets an array of daily minimum temperatures.",
			"the {NAME} minimum temperature for day {INDEX|0}", null)]
        [SupportsDriverPropertyBinding]
        public IScriptArray Lows {
            get {
				return new ScriptArrayMarshalByValue(m_forecasts.Select(f => f.GetLow()), 0);
            }
        }

        [ScriptObjectPropertyAttribute("Conditions", "Gets an array of daily weather conditions.",
			"the {NAME} weather condition for day {INDEX|0}", null)]
        [SupportsDriverPropertyBinding]
        public IScriptArray Conditions {
            get {
				return new ScriptArrayMarshalByValue(m_forecasts.Select(f => f.Condition), 0);
            }
        }

		// Weather Underground doesn't support this, but it is a required property.
		[ScriptObjectPropertyAttribute("Precipitation Chance", "Gets the percent precipitation chance.",
			"the percent precipitation chance", null)]
		[SupportsDriverPropertyBinding]
		public ScriptNumber PrecipitationChance {
			get {
				return new ScriptNumber(m_forecasts[0].Pop);
			}
		}

		// Chance of precipitation by day
		[ScriptObjectPropertyAttribute("Daily Precipitation Chance", "Gets an array of percent precipitation chance.",
			"the percent precipitation chance for day {INDEX|0}", null)]
		[SupportsDriverPropertyBinding]
		public IScriptArray PrecipitationChanceDay {
			get {
				return new ScriptArrayMarshalByValue(m_forecasts.Select(f => f.Pop), 0);
			}
		}


        [ScriptObjectPropertyAttribute("Dates", "Gets the dates for all the forecast days.",
			"the {NAME} date for forecast day {INDEX|0}", null)]
        [SupportsDriverPropertyBinding]
        public IScriptArray Dates {
            get {
                // return an array of ScriptDateTime elements for the dates, 0 based.
				ScriptArrayMarshalByValue array = new ScriptArrayMarshalByValue();
				for (int i = 0; i < m_forecasts.Length; i++) {
					array.Add(new ScriptDateTime(
						new ScriptNumber(m_forecasts[i].Year),
						new ScriptNumber(m_forecasts[i].Month),
						new ScriptNumber(m_forecasts[i].Day)));
				}
				return array;
            }
        }

        [ScriptObjectPropertyAttribute("Dates Text", "Gets the dates for all the forecast days as a text string.",
			"the {NAME} date for forecast day {INDEX|0}", null)]
        [SupportsDriverPropertyBinding]
        public IScriptArray DatesText {
            get {
                // return an array of ScriptString elements for the dates, 0 based.
				return new ScriptArrayMarshalByValue(m_forecasts.Select(f => f.DateText), 0);
            }
        }

        [ScriptObjectPropertyAttribute("Week Days", "Gets the day names for all the forecast days.",
			"the {NAME} day of week for forecast day {INDEX|0}", null)]
        [SupportsDriverPropertyBinding]
        public IScriptArray WeekDayTexts {
            get {
                // return an array of ScriptString elements for the week day name, 0 based.
				return new ScriptArrayMarshalByValue(m_forecasts.Select(f => f.WeekDay), 0);
            }
        }

        [ScriptObjectPropertyAttribute("Day Icon IDs", "Gets the day icon ID for all days.", 0, 47,
			"the {NAME} condition icon id for day {INDEX|0}", "set {NAME} to {INDEX|0}")]
        [SupportsDriverPropertyBinding]
        public IScriptArray DayIconIDs {
            get {
                // return a 0 based array of day icon ids
				return new ScriptArrayMarshalByValue(m_forecasts.Select(f => IconID(f.Icon)), 0);
            }
        }

        [ScriptObjectPropertyAttribute("Night Icon IDs", "Gets the night icon ID for all days.", 0, 47,
			"the {NAME} night time condition icon id for day {INDEX|0}", null)]
        [SupportsDriverPropertyBinding]
        public IScriptArray NightIconIDs {
            get {
                // return a 0 based array of night icon ids
				return new ScriptArrayMarshalByValue(m_forecasts.Select(f => IconID(f.NightIcon)), 0);
            }
        }

        [ScriptObjectPropertyAttribute("Condition Icon Urls", "Gets the condition icon URLs for all days.",
			"the {NAME} condition icon url for day {INDEX|0}", null)]
        [SupportsDriverPropertyBinding]
        public IScriptArray ConditionIconUrls {
            get {
                // return a 0 based array of condition icon urls
				return new ScriptArrayMarshalByValue(m_forecasts.Select(f => f.IconURL), 0);
            }
        }
#if false

        [ScriptObjectPropertyAttribute("Smiley Condition Icon Urls", "Gets an array of condition icons urls by day.",
			"the {NAME} condition icon url for day {INDEX|0}", null)]
        [SupportsDriverPropertyBinding]
        public IScriptArray SmileyConditionIconUrls {
            get {
                // return a 0 based array of condition icon urls
				return new ScriptArrayMarshalByValue(m_forecasts.Select(f => f.IconURLSmiley), 0);
            }
        }

        [ScriptObjectPropertyAttribute("Generic Condition Icon Urls", "Gets an array of condition icons urls by day.",
			"the {NAME} condition icon url for day {INDEX|0}", null)]
        [SupportsDriverPropertyBinding]
        public IScriptArray GenericConditionIconUrls {
            get {
                // return a 0 based array of condition icon urls
				return new ScriptArrayMarshalByValue(m_forecasts.Select(f => f.IconURLGeneric), 0);
            }
        }

        [ScriptObjectPropertyAttribute("Old School Condition Icon Urls", "Gets an array of condition icons urls by day.",
			"the {NAME} condition icon url for day {INDEX|0}", null)]
        [SupportsDriverPropertyBinding]
        public IScriptArray OldSchoolConditionIconUrls {
            get {
                // return a 0 based array of condition icon urls
				return new ScriptArrayMarshalByValue(m_forecasts.Select(f => f.IconURLOldSchool), 0);
            }
        }

        [ScriptObjectPropertyAttribute("Cartoon Condition Icon Urls", "Gets an array of condition icons urls by day.",
			"the {NAME} condition icon url for day {INDEX|0}", null)]
        [SupportsDriverPropertyBinding]
        public IScriptArray CartoonConditionIconUrls {
            get {
                // return a 0 based array of condition icon urls
				return new ScriptArrayMarshalByValue(m_forecasts.Select(f => f.IconURLCartoon), 0);
            }
        }

        [ScriptObjectPropertyAttribute("Mobile Condition Icon Urls", "Gets an array of condition icons urls by day.",
			"the {NAME} condition icon url for day {INDEX|0}", null)]
        [SupportsDriverPropertyBinding]
        public IScriptArray MobileConditionIconUrls {
            get {
                // return a 0 based array of condition icon urls
				return new ScriptArrayMarshalByValue(m_forecasts.Select(f => f.IconURLMobile), 0);
            }
        }

        [ScriptObjectPropertyAttribute("Simple Condition Icon Urls", "Gets an array of condition icons urls by day.",
			"the {NAME} condition icon url for day {INDEX|0}", null)]
        [SupportsDriverPropertyBinding]
        public IScriptArray SimpleConditionIconUrls {
            get {
                // return a 0 based array of condition icon urls
				return new ScriptArrayMarshalByValue(m_forecasts.Select(f => f.IconURLSimple), 0);
            }
        }

        [ScriptObjectPropertyAttribute("Contemporary Condition Icon Urls", "Gets an array of condition icons urls by day.",
			"the {NAME} condition icon url for day {INDEX|0}", null)]
        [SupportsDriverPropertyBinding]
        public IScriptArray ContemporaryConditionIconUrls {
            get {
                // return a 0 based array of condition icon urls
				return new ScriptArrayMarshalByValue(m_forecasts.Select(f => f.IconURLContemporary), 0);
            }
        }

        [ScriptObjectPropertyAttribute("Helen Condition Icon Urls", "Gets an array of condition icons urls by day.",
			"the {NAME} condition icon url for day {INDEX|0}", null)]
        [SupportsDriverPropertyBinding]
        public IScriptArray HelenConditionIconUrls {
            get {
                // return a 0 based array of condition icon urls
				return new ScriptArrayMarshalByValue(m_forecasts.Select(f => f.IconURLHelen), 0);
            }
        }

        [ScriptObjectPropertyAttribute("Incredible Condition Icon Urls", "Gets an array of condition icons urls by day.",
			"the {NAME} condition icon url for day {INDEX|0}", null)]
        [SupportsDriverPropertyBinding]
        public IScriptArray IncredibleConditionIconUrls {
            get {
                // return a 0 based array of condition icon urls
				return new ScriptArrayMarshalByValue(m_forecasts.Select(f => f.IconURLIncredible), 0);
            }
        }
#endif

        [ScriptObjectPropertyAttribute("Day Descriptions", "Gets an array of daily forecasts by day.",
			"the {NAME} forecast text day {INDEX|0}", null)]
        [SupportsDriverPropertyBinding]
        public IScriptArray DayDescriptions {
            get {
				return new ScriptArrayMarshalByValue(m_forecasts.Select(f => f.DayForecastText), 0);
            }
        }

        [ScriptObjectPropertyAttribute("Night Descriptions", "Gets an array of nightly forecasts by day.",
			"the {NAME} night forecast text day {INDEX|0}", null)]
        [SupportsDriverPropertyBinding]
        public IScriptArray NightDescriptions {
            get {
				return new ScriptArrayMarshalByValue(m_forecasts.Select(f => f.NightForecastText), 0);
            }
        }

        [ScriptObjectPropertyAttribute("Night Conditions", "Gets an array of nightly conditions by day.",
			"the {NAME} night condition text day {INDEX|0}", null)]
        [SupportsDriverPropertyBinding]
        public IScriptArray NightConditions {
            get {
				return new ScriptArrayMarshalByValue(m_forecasts.Select(f => f.NightCondition), 0);
            }
        }

        [ScriptObjectPropertyAttribute("Wind Gust Speeds", "Gets an array of wind gust speeds by day.",
			"the {NAME} wind gust speed {INDEX|0}", null)]
        [SupportsDriverPropertyBinding]
        public IScriptArray WindGustSpeeds {
            get {
				return new ScriptArrayMarshalByValue(m_forecasts.Select(f => f.GetWindGust()), 0);
            }
        }

        [ScriptObjectPropertyAttribute("Wind Gust Direction", "Gets an array of wind gust direction by day.",
			"the {NAME} wind gust direction {INDEX|0}", null)]
        [SupportsDriverPropertyBinding]
        public IScriptArray WindGustDirection {
            get {
				return new ScriptArrayMarshalByValue(m_forecasts.Select(f => f.WindMaxDirection), 0);
            }
        }

        [ScriptObjectPropertyAttribute("Wind Gust Degrees", "Gets an array of wind gust degrees by day.",
			"the {NAME} wind gust degrees {INDEX|0}", null)]
        [SupportsDriverPropertyBinding]
        public IScriptArray WindGustDegrees {
            get {
				return new ScriptArrayMarshalByValue(m_forecasts.Select(f => f.WindMaxDegrees), 0);
            }
        }

        [ScriptObjectPropertyAttribute("Wind Speeds", "Gets an array of wind speeds by day.",
			"the {NAME} wind speed {INDEX|0}", null)]
        [SupportsDriverPropertyBinding]
        public IScriptArray WindSpeeds {
            get {
				return new ScriptArrayMarshalByValue(m_forecasts.Select(f => f.GetWindSpeed()), 0);
            }
        }

        [ScriptObjectPropertyAttribute("Wind Direction", "Gets an array of wind direction by day.",
			"the {NAME} wind direction {INDEX|0}", null)]
        [SupportsDriverPropertyBinding]
        public IScriptArray WindDirection {
            get {
				return new ScriptArrayMarshalByValue(m_forecasts.Select(f => f.WindAvgDirection), 0);
            }
        }

        [ScriptObjectPropertyAttribute("Wind Degrees", "Gets an array of wind degrees by day.",
			"the {NAME} wind degrees {INDEX|0}", null)]
        [SupportsDriverPropertyBinding]
        public IScriptArray WindDegrees {
            get {
				return new ScriptArrayMarshalByValue(m_forecasts.Select(f => f.WindAvgDegrees), 0);
            }
        }

        [ScriptObjectPropertyAttribute("Average Humidity", "Gets an array of average humidity by day.",
			"the {NAME} average humidity {INDEX|0}", null)]
        [SupportsDriverPropertyBinding]
        public IScriptArray AverageHumidity {
            get {
				return new ScriptArrayMarshalByValue(m_forecasts.Select(f => f.AvgHumidity), 0);
            }
        }

        [ScriptObjectPropertyAttribute("Minimum Humidity", "Gets an array of minimum humidity by day.",
			"the {NAME} minimum humidity {INDEX|0}", null)]
        [SupportsDriverPropertyBinding]
        public IScriptArray MinimumHumidity {
            get {
				return new ScriptArrayMarshalByValue(m_forecasts.Select(f => f.MinHumidity), 0);
            }
        }

        [ScriptObjectPropertyAttribute("Maximum Humidity", "Gets an array of maximum humidity by day.",
			"the {NAME} maximum humidity {INDEX|0}", null)]
        [SupportsDriverPropertyBinding]
        public IScriptArray MaximumHumidity {
            get {
				return new ScriptArrayMarshalByValue(m_forecasts.Select(f => f.MaxHumidity), 0);
            }
        }

		[ScriptObjectPropertyAttribute("Last Forecast Update", "Date and Time that last update was recieved.",
			"the {NAME} forecast data feed observation time", null)]
		[SupportsDriverPropertyBinding]
		public ScriptString LastForecastUpdate {
			get {
				return new ScriptString(m_forecasts[0].LastUpdate);
			}
		}

        [ScriptObjectPropertyAttribute("Sunrise", "Gets the current sunrise time.",
			"the {NAME} sunrise", null)]
        [SupportsDriverPropertyBinding]
        public IScriptArray Sunrise {
            get {
				return new ScriptArrayMarshalByValue(m_forecasts.Select(f => f.Sunrise), 0);
            }
        }

        [ScriptObjectPropertyAttribute("Sunset", "Gets the current sunset time.",
			"the {NAME} sunset", null)]
        [SupportsDriverPropertyBinding]
        public IScriptArray Sunset {
            get {
				return new ScriptArrayMarshalByValue(m_forecasts.Select(f => f.Sunset), 0);
            }
        }


		//
		// ----------------------------------------------------------------
		//  Driver private methods
		// ----------------------------------------------------------------
		//

		//
		// Safe parse routine for parsing number'from XML.  Don't
		// want bad XML contents to break getting the data feed
		//
		private double DParse(string str) {
			try {
				if (str.TrimEnd(null).EndsWith("%")) {
					return double.Parse(str.Remove(str.LastIndexOf('%'), 1));
				} else {
					return double.Parse(str);
				}
			} catch {
				return 0.0;
			}
		}

		private int IParse(string str) {
			try {
				return int.Parse(str);
			} catch {
				return 0;
			}
		}

		//
		// Pull data from the Weather Underground web site and parse
		// the returned XML.
		//
		private bool ReadWeatherData(string station) {
			XmlDocument xml = new XmlDocument();
			string url;
			XmlNode node;
			bool err = true;

			Logger.Debug("Read data from web site.");
			url =
				"http://api.wunderground.com/api/" + key +
				"/" + FEATURES.conditions.ToString() +  "/" +
				"/" + FEATURES.forecast7day.ToString() +  "/" +
				"/" + FEATURES.astronomy.ToString() +  "/" +
				"q/" + station + ".xml";

			// Send the HTTP request and get the XML response.
			try {
				xml.Load(url);
			} catch (Exception ex) {
				Logger.Error("Error " + ex.Message + " while loading " + url);
				return false;
			}

			if (xml.InnerText == "") {
				Logger.Error("Empty XML string.");
			}

			// Parse the XML 
			try {
				node = xml.ChildNodes[0];
				Logger.Debug(" ChildNodes[0] -> " + node.Name + "  : " + node.InnerText);

				// This is the <response> level.  Loop through this level looking
				// for the features we want to process.

				foreach (XmlNode n in node.ChildNodes) {
					Logger.Debug(" -> " + n.Name + "  : " + n.InnerText);
					switch (n.Name) {
						case "version": // Version of the API
							break;
						case "termsofService": // URL for the terms of service
							break;
						case "features": // List of features reported in this query
							break;
						case "forecast":
							err = ParseForecastData(n);
							break;
						case "current_observation":
							err = ParseConditionData(n);
							break;
						case "moon_phase":
							err = ParseAstronomyData(n);
							break;
					}
				}
			} catch (Exception ex) {
				Logger.Error("XML parsing failed: " + ex.Message);
				return false;
			}
			return err;
		}


		private bool ParseConditionData(XmlNode conditions) {
			try {
				foreach (XmlNode n in conditions.ChildNodes) {
					Logger.Debug(" -> " + n.Name + "  : " + n.InnerText);
					switch (n.Name) {
						case "credit": m_weather.Credit = n.InnerText; break;
						case "credit_URL": m_weather.CreditURL = n.InnerText; break;
						case "image":
							Logger.Error("Processing image");
							m_weather.CreditURL = n.SelectNodes(".//url")[0].InnerText;
							m_weather.Credit = n.SelectNodes(".//title")[0].InnerText;
							break;
						case "location":
							Logger.Error("Processing location");
							// Location has multiple child nodes!
							m_weather.Location = n.SelectNodes(".//full")[0].InnerText;
							m_weather.LocationCity = n.SelectNodes(".//city")[0].InnerText;
							m_weather.LocationState = n.SelectNodes(".//state")[0].InnerText;
							m_weather.LocationNeighborhood = n.SelectNodes(".//neighborhood")[0].InnerText;
							m_weather.LocationZip = n.SelectNodes(".//zip")[0].InnerText;

							try {
								m_weather.ForecastLoc = n.SelectNodes(".//latitude")[0].InnerText;
								m_weather.ForecastLoc += "," + n.SelectNodes(".//longitude")[0].InnerText;
								if (m_station_location == "") {
									m_station_location = m_weather.ForecastLoc;
								}
							} catch (Exception ex1) {
								Logger.Error("Failed to parse location: " + ex1.Message);
								m_weather.ForecastLoc = "";
							}
							break;
						case "display_location": // Airport queries use this instead of location
							Logger.Error("Processing display_location");
							// Location has multiple child nodes!
							m_weather.Location = n.SelectNodes(".//full")[0].InnerText;
							m_weather.LocationCity = n.SelectNodes(".//city")[0].InnerText;
							m_weather.LocationState = n.SelectNodes(".//state_name")[0].InnerText;
							m_weather.LocationZip = n.SelectNodes(".//zip")[0].InnerText;
							m_weather.Elevation = n.SelectNodes(".//elevation")[0].InnerText;
							// Location has country and elevation if we want it.

							try {
								m_weather.ForecastLoc = n.SelectNodes(".//latitude")[0].InnerText;
								m_weather.ForecastLoc += "," + n.SelectNodes(".//longitude")[0].InnerText;
								if (m_station_location == "") {
									m_station_location = m_weather.ForecastLoc;
								}
							} catch (Exception ex1) {
								Logger.Error("Failed to parse location: " + ex1.Message);
								m_weather.ForecastLoc = "";
							}
							break;
						case "observation_location": // Where the data is coming from?
							Logger.Error("Processing observation_location");
							m_weather.Location = n.SelectNodes(".//full")[0].InnerText;
							m_weather.LocationCity = n.SelectNodes(".//city")[0].InnerText;
							m_weather.LocationState = n.SelectNodes(".//state")[0].InnerText;
							m_weather.Elevation = n.SelectNodes(".//elevation")[0].InnerText;
							// Location has country and elevation if we want it.

							try {
								Logger.Error("Get location from observation location!");
								m_weather.Latitude = DParse(n.SelectNodes(".//latitude")[0].InnerText);
								m_weather.Longitude = DParse(n.SelectNodes(".//longitude")[0].InnerText);

								m_weather.ForecastLoc = n.SelectNodes(".//latitude")[0].InnerText;
								m_weather.ForecastLoc += "," + n.SelectNodes(".//longitude")[0].InnerText;
								if (m_station_location == "") {
									m_station_location = m_weather.ForecastLoc;
								}
							} catch (Exception ex1) {
								Logger.Error("Failed to parse location: " + ex1.Message);
								m_weather.ForecastLoc = "";
							}
							break;
						case "observation_time": m_weather.LastUpdate = n.InnerText; break;
						case "observation_time_rfc822": break;
						case "observation_epoch": break;
						case "local_time_rfc822": break;
						case "local_epoch": break;
						case "local_tz_short": break;
						case "local_tz_long": break;
						case "temperature_string": m_weather.TemperatureString = n.InnerText; break;
						case "temp_f": m_weather.Temperature = DParse(n.InnerText); break;
						case "temp_c": m_weather.Temperature_c = DParse(n.InnerText); break;
						case "relative_humidity": m_weather.Humidity = DParse(n.InnerText); break;
						case "wind_string": break;
						case "wind_dir": m_weather.WindDirection = n.InnerText; break;
						case "wind_degrees": m_weather.WindDegrees = IParse(n.InnerText); break;
						case "wind_mph": m_weather.WindSpeed = DParse(n.InnerText); break;
						case "wind_gust_mph": m_weather.WindGust = DParse(n.InnerText); break;
						case "pressure_string": m_weather.PressureString = n.InnerText; break;
						case "pressure_in": m_weather.PressureIN = DParse(n.InnerText); break;
						case "pressure_mb": m_weather.PressureMB = DParse(n.InnerText); break;
						case "pressure_trend": m_weather.PressureTrending = n.InnerText; break;
						case "dewpoint_string": m_weather.DewpointString = n.InnerText; break;
						case "dewpoint_f": m_weather.Dewpoint = DParse(n.InnerText); break;
						case "dewpoint_c": m_weather.Dewpoint_c = DParse(n.InnerText); break;
						case "precip_today_in": m_weather.Precipitation = DParse(n.InnerText); break;
						case "precip_today_metric":
							try {
								m_weather.Precipitation_cm = DParse(n.InnerText.Split(' ')[0]);
							} catch {
								Logger.Error("Bad XML format for precip_today_metric: [" + n.InnerText.Split(' ')[0] + "]");
								m_weather.Precipitation_cm = 0.0;
							}
							break;
						case "precip_1hr_string": break;
						case "precip_1hr_in": break;
						case "precip_1hr_metric": break;
						case "heat_index_f": m_weather.HeatIndex_f = DParse(n.InnerText);  break;
						case "heat_index_c": m_weather.HeatIndex_c = DParse(n.InnerText);  break;
						case "heat_index_string": m_weather.HeatIndexString = n.InnerText;  break;
						case "windchill_f": m_weather.WindChill_c = DParse(n.InnerText);  break;
						case "windchill_c": m_weather.WindChill_c = DParse(n.InnerText);  break;
						case "windchill_string": m_weather.WindChillString = n.InnerText;  break;
						case "visibility_mi": m_weather.Visibility_mi = DParse(n.InnerText);  break;
						case "visibility_km": m_weather.Visibility_km = DParse(n.InnerText);  break;
						case "solar_radiation": m_weather.SolarRadiation = DParse(n.InnerText);  break;
						case "UV": m_weather.UV = DParse(n.InnerText);  break;
						case "station_id": m_weather.Station = n.InnerText;  break;
						case "station_type": m_weather.StationType = n.InnerText;  break;
						case "weather": m_weather.Weather = n.InnerText;  break;
						case "ob_url":
							// Forcast data
							Logger.Info("  -> ob_url = " + n.InnerText);
							break;
						default:
							break;
					}
				}

			} catch (Exception ex) {
				Logger.Error("XML parsing failed: " + ex.Message);
				return false;
			}

			return true;
		}

		private bool ParseAstronomyData(XmlNode moon_phase) {

			try {
				foreach (XmlNode n in moon_phase.ChildNodes) {
					Logger.Debug(" -> " + n.Name + "  : " + n.InnerText);
					switch (n.Name) {
						case "percentIlluminated":
							m_weather.MoonLight = n.InnerText;
							break;
						case "ageOfMoon":
							m_weather.MoonAge = n.InnerText;
							break;
						case "sunset":
							m_weather.Sunset = n.SelectNodes(".//hour")[0].InnerText + ":" +
								n.SelectNodes(".//minute")[0].InnerText;
							break;
						case "sunrise":
							m_weather.Sunrise = n.SelectNodes(".//hour")[0].InnerText + ":" +
								n.SelectNodes(".//minute")[0].InnerText;
							break;
						case "current_time":
							break;
					}
				}
			} catch (Exception ex) {
				Logger.Error("Moon phase parsing failed: " + ex.Message);
				return false;
			}

			// Calculate and populate sunset and sunrise times.
			DateTime date;
			date = new DateTime(DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day);
			for (int i = 0; i < 8; i++) {
				SolarInfo.Logger = Logger;
				SolarInfo si = SolarInfo.ForDate(m_weather.Latitude, m_weather.Longitude, date);
				m_forecasts[i].Sunset = si.Sunset.ToShortTimeString();
				m_forecasts[i].Sunrise = si.Sunrise.ToShortTimeString();

				date = date.AddDays(1.0);
			}

			return true;
		}


		//
		// Query for forecast data.  How often should we do this query?
		//
		private bool ParseForecastData(XmlNode forecast) {
			XmlNodeList nl;
			XmlNode fc_type;
			int period = 0;

			// Parse forecast XML.  The forecast consist of two major sections
			// the <forecast_txt> and the <simpleforecast> each of these is
			// broken down into periods.
			// <forecast_txt> has a text description of the forecast for a
			//   period, the period can be today, tonight, rest of today, etc.
			// <simpleforecast> has all the data, high's,low's, etc. It is also
			//   broken down by period, with one period per day.
			//
			// NOTE: The number of days worth of data returned seems to very. It
			//       can be 7 days or 8 days.
			try {
				// Expect children
				//   txt_forecast
				//   simpleforecast

				// Start with the simpleforeast.
				//   Count how many periods are present and re-allocate
				//   the forecast array to match.
				fc_type = forecast.SelectSingleNode(".//simpleforecast");
				nl = fc_type.SelectNodes(".//period");
				Logger.Debug("*** Forecast has " + nl.Count.ToString() + " periods.");
				if (nl.Count > 0) {
					// Allocate a new array of forecast data and create 
					// the foreacast objects.
					m_forecasts = new Forecast[nl.Count];
					for (int i = 0; i < m_forecasts.Length; i++) {
						m_forecasts[i] = new Forecast(m_metric);
					}
					m_weather.ForecastDays = nl.Count;

					try {
						nl = fc_type.SelectNodes(".//forecastday");
						foreach (XmlNode f in nl) {
							Logger.Debug(f.Name + " -- " + f.InnerXml);

							f.CreateNavigator();

							// Period defines which slot in the forecast array this is for.
							period = IParse(f.SelectNodes(".//period")[0].InnerText);
							//Logger.Debug("   -> period = " + period.ToString());

							// Date
							try {
								m_forecasts[period - 1].DateText =
									f.SelectNodes(".//date/monthname")[0].InnerText + " " +
									f.SelectNodes(".//date/day")[0].InnerText + ", " +
									f.SelectNodes(".//date/year")[0].InnerText;

								m_forecasts[period - 1].Year = IParse(f.SelectNodes(".//date/year")[0].InnerText);
								m_forecasts[period - 1].Month = IParse(f.SelectNodes(".//date/month")[0].InnerText);
								m_forecasts[period - 1].Day = IParse(f.SelectNodes(".//date/day")[0].InnerText);

								m_forecasts[period - 1].WeekDay = f.SelectNodes(".//date/weekday")[0].InnerText;
								// Other fields available are:
								//  epoch, pretty_short, pretty, yday, hour, min, sec, isdst, weekday_short,
								//  ampm, tz_short, tz_long
							} catch (Exception ex) {
								Logger.Error("Failed to parse date:" + ex.Message);
							}

							// High temps
							try {
								m_forecasts[period - 1].High_f = DParse(f.SelectNodes(".//high/fahrenheit")[0].InnerText);
								m_forecasts[period - 1].High_c = DParse(f.SelectNodes(".//high/celsius")[0].InnerText);
							} catch (Exception ex) {
								Logger.Error("Failed to parse high temps:" + ex.Message);
							}

							// Low temps
							try {
								m_forecasts[period - 1].Low_f = DParse(f.SelectNodes(".//low/fahrenheit")[0].InnerText);
								m_forecasts[period - 1].Low_c = DParse(f.SelectNodes(".//low/celsius")[0].InnerText);
							} catch (Exception ex) {
								Logger.Error("Failed to parse low temps:" + ex.Message);
							}

							// Condition string
							try {
								m_forecasts[period - 1].Condition = f.SelectNodes(".//conditions")[0].InnerText;
							} catch (Exception ex) {
								Logger.Error("Failed to parse condition:" + ex.Message);
							}

							// Icon string
							try {
								m_forecasts[period - 1].Icon = f.SelectNodes(".//icon")[0].InnerText;
								// TODO: Convert the icon to an ID number for standard weather Icons.
							} catch (Exception ex) {
								Logger.Error("Failed to parse icon:" + ex.Message);
							}

							// skyicon string
							try {
								m_forecasts[period - 1].SkyIcon = f.SelectNodes(".//skyicon")[0].InnerText;
							} catch (Exception ex) {
								Logger.Error("Failed to parse sky icon:" + ex.Message);
							}

							// icon URL. Currently using default.  This is for the personal weather station.
							// Then new API only provides one icon_url
							try {
								m_forecasts[period - 1].IconURL = f.SelectNodes(".//icons/icon_set[@name='Default']/icon_url")[0].InnerText;
								m_forecasts[period - 1].IconURLSmiley = f.SelectNodes(".//icons/icon_set[@name='Smiley']/icon_url")[0].InnerText;
								m_forecasts[period - 1].IconURLGeneric = f.SelectNodes(".//icons/icon_set[@name='Generic']/icon_url")[0].InnerText;
								m_forecasts[period - 1].IconURLOldSchool = f.SelectNodes(".//icons/icon_set[@name='Old School']/icon_url")[0].InnerText;
								m_forecasts[period - 1].IconURLCartoon = f.SelectNodes(".//icons/icon_set[@name='Cartoon']/icon_url")[0].InnerText;
								m_forecasts[period - 1].IconURLMobile = f.SelectNodes(".//icons/icon_set[@name='Mobile']/icon_url")[0].InnerText;
								m_forecasts[period - 1].IconURLSimple = f.SelectNodes(".//icons/icon_set[@name='Simple']/icon_url")[0].InnerText;
								m_forecasts[period - 1].IconURLContemporary = f.SelectNodes(".//icons/icon_set[@name='Contemporary']/icon_url")[0].InnerText;
								m_forecasts[period - 1].IconURLHelen = f.SelectNodes(".//icons/icon_set[@name='Helen']/icon_url")[0].InnerText;
								m_forecasts[period - 1].IconURLIncredible = f.SelectNodes(".//icons/icon_set[@name='Incredible']/icon_url")[0].InnerText;
								//m_forecasts[period - 1].IconURL = f.SelectNodes(".//icons/icon_set[@name='Minimalist']/icon_url")[0].InnerText;
							} catch {
								try {
									m_forecasts[period - 1].IconURL = f.SelectNodes(".//icon_url")[0].InnerText;
								} catch (Exception ex) {
									Logger.Error("Failed to parse icon url:" + ex.Message);
								}
							}

							// precipitation percent
							try {
								m_forecasts[period - 1].Pop = IParse(f.SelectNodes(".//pop")[0].InnerText);
							} catch (Exception ex) {
								Logger.Error("Failed to parse pop value: " + ex.Message);
							}

							// maxwind
							try {
								m_forecasts[period - 1].WindMax_mph = DParse(f.SelectNodes(".//maxwind/mph")[0].InnerText);
								m_forecasts[period - 1].WindMax_kph = DParse(f.SelectNodes(".//maxwind/kph")[0].InnerText);
								m_forecasts[period - 1].WindMaxDirection = f.SelectNodes(".//maxwind/dir")[0].InnerText;
								m_forecasts[period - 1].WindMaxDegrees = DParse(f.SelectNodes(".//maxwind/degrees")[0].InnerText);
							} catch (Exception ex) {
								Logger.Error("Failed to parse maximum wind values: " + ex.Message);
							}

							// avewind
							try {
								m_forecasts[period - 1].WindAvg_mph = DParse(f.SelectNodes(".//avewind/mph")[0].InnerText);
								m_forecasts[period - 1].WindAvg_kph = DParse(f.SelectNodes(".//avewind/kph")[0].InnerText);
								m_forecasts[period - 1].WindAvgDirection = f.SelectNodes(".//avewind/dir")[0].InnerText;
								m_forecasts[period - 1].WindAvgDegrees = DParse(f.SelectNodes(".//avewind/degrees")[0].InnerText);
							} catch (Exception ex) {
								Logger.Error("Failed to parse average wind values: " + ex.Message);
							}

							// avehumidity
							try {
								m_forecasts[period - 1].AvgHumidity = DParse(f.SelectNodes(".//avehumidity")[0].InnerText);
								m_forecasts[period - 1].MaxHumidity = DParse(f.SelectNodes(".//maxhumidity")[0].InnerText);
								m_forecasts[period - 1].MinHumidity = DParse(f.SelectNodes(".//minhumidity")[0].InnerText);
							} catch (Exception ex) {
								Logger.Error("Failed to parse humidity values: " + ex.Message);
							}

							m_forecasts[period - 1].LastUpdate = DateTime.Now.ToString();
						} // End of for loop
					} catch {
						Logger.Info("Failed to parse the forecast data.");
					}
				} // End of if > 0


				// Process txt_forecast section.
				fc_type = forecast.SelectSingleNode(".//txt_forecast");
				nl = fc_type.SelectNodes(".//period");
				Logger.Debug("*** Forecast has " + nl.Count.ToString() + " periods.");
				try {

					// The text format block provides a text description of the
					// forecast for the current period plus the next 4 periods.
					// A period seems to be 1/2 a day.  I think the mapping is
					// as follows:
					//   "Rest of Today"   -  period #1
					//   "Tonight"         -  period #2
					//   "<Weekday>"       -  period #3
					//   "<Weekday> Night" -  period #4
					//   "<Weekday>"       -  period #5
					//
					// Is the above mapping always true or does it change throughout
					// the course of the day?  It changes at some point so that
					// period 1 is Tonight.
					//
					// Use this to get the icons for tonight and tomorrow night only!
					int index = 0;
					string text;
					string title;
					string txt_period;

					nl = fc_type.SelectNodes(".//forecastday");
					foreach (XmlNode f in nl) {

						Logger.Debug(f.Name + " -- " + f.InnerXml);

						f.CreateNavigator();

						title = f.SelectNodes(".//title")[0].InnerText;
						text = f.SelectNodes(".//fcttext")[0].InnerText;
						txt_period = f.SelectNodes(".//period")[0].InnerText;

						Logger.Debug("Forecast: " + txt_period + " / " + title + " maps to index " + index.ToString());

						// Look for titles that contain [N]ight
						if (title.Contains("ight")) {
							m_forecasts[index].NightForecastText = text;
							m_forecasts[index].NightForecastTitle = title;
							m_forecasts[index].NightIcon = f.SelectNodes(".//icon")[0].InnerText;
							m_forecasts[index].NightCondition = IconNameToCondition(m_forecasts[index].NightIcon);
							index++;
						} else {
							m_forecasts[index].DayForecastText = text;
							m_forecasts[index].DayForecastTitle = title;
						}
					} // end of for
				} catch (Exception ex) {
					Logger.Error("Parsing of txt_forecast Failed: " + ex.Message);
				}
			} catch (Exception ex) {
				Logger.Error("Forecast Parsing Failed: " + ex.Message);
			}

			Logger.Debug("Finished parsing forecast data.");
			return true;
		}


		//
		// Thread to read data from the Brultech energy monitor
		// This should poll the monitor and then do any necessary 
		// processing of the data.
		//
		private void PollWUnderground(Object sender, EventArgs e) {

			if (ReadWeatherData(m_location)) {
				ApparentTemp();

				DevicePropertyChangeNotification("BarometricPressure", m_weather.GetPressure);
				DevicePropertyChangeNotification("BarometricTrend", m_weather.PressureTrend);
				DevicePropertyChangeNotification("BarometricUnits", m_weather.PressureUnits);
				DevicePropertyChangeNotification("Credit", m_weather.Credit);
				DevicePropertyChangeNotification("CreditURL", m_weather.CreditURL);
				DevicePropertyChangeNotification("DewPoint", m_weather.GetDewpoint());
				DevicePropertyChangeNotification("HeatIndex", m_weather.GetHeatIndex());
				DevicePropertyChangeNotification("Humidity", m_weather.Humidity);
				DevicePropertyChangeNotification("LastUpdate", m_weather.LastUpdate);
				DevicePropertyChangeNotification("Location", m_weather.Location);
				DevicePropertyChangeNotification("Precipitation", m_weather.GetPrecipitation());
				DevicePropertyChangeNotification("SolarRadiation", m_weather.SolarRadiation);
				//DevicePropertyChangeNotification("Sunset", m_weather.Sunset);
				//DevicePropertyChangeNotification("Sunrise", m_weather.Sunrise);
				DevicePropertyChangeNotification("Temperature", m_weather.GetTemperature());
				DevicePropertyChangeNotification("UVIndex", m_weather.UV);
				DevicePropertyChangeNotification("WindDirectionDegrees", m_weather.WindDegrees);
				DevicePropertyChangeNotification("WindDirectionText", m_weather.WindDirection);
				DevicePropertyChangeNotification("WindGustSpeed", m_weather.WindGust);
				DevicePropertyChangeNotification("WindSpeed", m_weather.WindSpeed);
				DevicePropertyChangeNotification("Windchill", m_weather.GetWindChill());
				DevicePropertyChangeNotification("ApparentTemperature", m_weather.GetApparentTemperature());
				DevicePropertyChangeNotification("Visibility", m_weather.GetVisibility);
				DevicePropertyChangeNotification("Elevation", m_weather.Elevation);

				// Only update based on number of days we have data for.
				for (int i = 0; i < m_weather.ForecastDays; i++) {
					DevicePropertyChangeNotification("Highs", i, m_forecasts[i].GetHigh());
					DevicePropertyChangeNotification("Lows", i, m_forecasts[i].GetLow());
					DevicePropertyChangeNotification("Conditions", i, m_forecasts[i].Condition);
					DevicePropertyChangeNotification("Dates", i,
						new DateTime(m_forecasts[i].Year, m_forecasts[i].Month, m_forecasts[i].Day));
					DevicePropertyChangeNotification("DatesText", i, m_forecasts[i].DateText);
					DevicePropertyChangeNotification("WeekDayTexts", i, m_forecasts[i].WeekDay);
					DevicePropertyChangeNotification("DayIconIDs", i, IconID(m_forecasts[i].Icon));
					DevicePropertyChangeNotification("NightIconIDs", i, IconID(m_forecasts[i].NightIcon));
					DevicePropertyChangeNotification("ConditionIconUrls", i, m_forecasts[i].IconURL);
					DevicePropertyChangeNotification("SmileyConditionIconUrls", i, m_forecasts[i].IconURLSmiley);
					DevicePropertyChangeNotification("GenericConditionIconUrls", i, m_forecasts[i].IconURLGeneric);
					DevicePropertyChangeNotification("OldSchoolConditionIconUrls", i, m_forecasts[i].IconURLOldSchool);
					DevicePropertyChangeNotification("CartoonConditionIconUrls", i, m_forecasts[i].IconURLCartoon);
					DevicePropertyChangeNotification("MobileConditionIconUrls", i, m_forecasts[i].IconURLMobile);
					DevicePropertyChangeNotification("SimpleConditionIconUrls", i, m_forecasts[i].IconURLSimple);
					DevicePropertyChangeNotification("ContemporaryConditionIconUrls", i, m_forecasts[i].IconURLContemporary);
					DevicePropertyChangeNotification("HelenConditionIconUrls", i, m_forecasts[i].IconURLHelen);
					DevicePropertyChangeNotification("IncredibleConditionIconUrls", i, m_forecasts[i].IconURLIncredible);
					DevicePropertyChangeNotification("DayDescriptions", i, m_forecasts[i].DayForecastText);
					DevicePropertyChangeNotification("NightDescriptions", i, m_forecasts[i].NightForecastText);
					DevicePropertyChangeNotification("PrecipitationChanceDay", i, m_forecasts[i].Pop);
					DevicePropertyChangeNotification("NightConditions", i, m_forecasts[i].NightCondition);
					DevicePropertyChangeNotification("WindGustSpeeds", i, m_forecasts[i].GetWindGust());
					DevicePropertyChangeNotification("WindGustDirection", i, m_forecasts[i].WindMaxDirection);
					DevicePropertyChangeNotification("WindGustDegrees", i, m_forecasts[i].WindMaxDegrees);
					DevicePropertyChangeNotification("WindSpeeds", i, m_forecasts[i].GetWindSpeed());
					DevicePropertyChangeNotification("WindSpeedDirection", i, m_forecasts[i].WindAvgDirection);
					DevicePropertyChangeNotification("WindSpeedDegrees", i, m_forecasts[i].WindAvgDegrees);
					DevicePropertyChangeNotification("AverageHumidity", i, m_forecasts[i].AvgHumidity);
					DevicePropertyChangeNotification("MinimumHumidity", i, m_forecasts[i].MinHumidity);
					DevicePropertyChangeNotification("MaximumHumidity", i, m_forecasts[i].MaxHumidity);
					DevicePropertyChangeNotification("Sunrise", i, m_forecasts[i].Sunrise);
					DevicePropertyChangeNotification("Sunset", i, m_forecasts[i].Sunset);
				}

				// Not an array
				DevicePropertyChangeNotification("PrecipitationChance", m_forecasts[0].Pop);
				DevicePropertyChangeNotification("LastForecastUpdate", m_forecasts[0].LastUpdate);
			}
		}


		//
		// Formula:
		//   water_vapor_pressure = relative_humidity / 100 * 6.105 * math.exp(17.27 * temp_c / (237.7 + temp_c))
		//   at = temp_c + (0.33 * water_vapor_pressure) - (0.70 * wind speed) - 4
		// wind speed is in meter/s
		//
		internal void ApparentTemp() {
			double wv;
			double ws;

			ws = m_weather.WindSpeed / 2.2368; // convert mph to m/s
			wv = m_weather.Humidity / 100 * 6.105 * Math.Exp(17.27 * m_weather.Temperature_c / (237.7 + m_weather.Temperature_c));

			m_weather.ApparentTemp_c = m_weather.Temperature_c + (0.33 * wv) - (0.70 * ws) - 4.0;

			// convert temp from C to F
			m_weather.ApparentTemp_f = Math.Round((m_weather.ApparentTemp_c * 1.8) + 32, 1);
			m_weather.ApparentTemp_c = Math.Round(m_weather.ApparentTemp_c, 1);
		}

		// Convert icon names to icon ID's
		// 
		// sunny/cloudy icons from clear to overcast:
		// day   32 34 30 28 26
		// night 31 33 29 27 26
		// 
		// sunny         d32 n31
		// mostly sunny  d34 n33
		// partly sunny  d30 n29
		// partly cloudy d30 n29
		// mostly cloudy d28 n27
		// cloudy        d26 n26

		internal int IconID(string icon) {
			switch (icon) {
				case "clear": return 32;
				case "flurries": return 14;
				case "fog": return 20;
				case "hazy": return 21;
				case "cloudy": return 26;
				case "mostlycloudy": return 28;
				case "partlycloudy": return 30;
				case "partlysunny": return 30;
				case "mostlysunny": return 34;
				case "rain": return 40;
				case "sleet": return 5;
				case "snow": return 41;
				case "sunny": return 32;
				case "tstorms": return 17;
				case "chanceflurries": return 13;
				case "chancerain": return 39;
				case "chancesleet": return 5;
				case "chancesnow": return 41;
				case "chancetstorms": return 17;
				case "unknown": return 44;
				case "nt_clear": return 31;
				case "nt_cloudy": return 26;
				case "nt_flurries": return 46;
				case "nt_fog": return 20;
				case "nt_hazy": return 21;
				case "nt_mostlycloudy": return 27;
				case "nt_partlycloudy": return 29;
				case "nt_partlysunny": return 29;
				case "nt_mostlysunny": return 33;
				case "nt_rain": return 45;
				case "nt_sleet": return 5;
				case "nt_snow": return 42;
				case "nt_sunny": return 31;
				case "nt_tstorms": return 47;
				default:
					Logger.Debug("No IconID match for icon named [" + icon + "]");
					return 44; // 44 = Unknown
			}
		}

		internal string IconNameToCondition(string icon) {
			switch (icon) {
				case "clear": return "Clear";
				case "flurries": return "Snow Flurries";
				case "fog": return "Foggy";
				case "hazy": return "Hazy";
				case "cloudy": return "Cloudy";
				case "mostlycloudy": return "Mostly Cloudy";
				case "partlycloudy": return "Partly Cloudy";
				case "partlysunny": return "Partly Sunny";
				case "mostlysunny": return "Mostly Sunny";
				case "rain": return "Rain";
				case "sleet": return "Sleet";
				case "snow": return "Snow";
				case "sunny": return "Clear";
				case "tstorms": return "Thunderstorms";
				case "chanceflurries": return "Chance of Flurries";
				case "chancerain": return "Chance of Rain";
				case "chancesleet": return "Chance of Sleet";
				case "chancesnow": return "Chance of Snow";
				case "chancetstorms": return "Chance of Thunderstorms";
				case "unknown": return "Unknown";
				case "nt_clear": return "Clear";
				case "nt_cloudy": return "Cloudy";
				case "nt_flurries": return "Flurries";
				case "nt_fog": return "Foggy";
				case "nt_hazy": return "Hazy";
				case "nt_mostlycloudy": return "Mostly Cloudy";
				case "nt_partlycloudy": return "Partly Cloudy";
				case "nt_partlysunny": return "Partly Clear";
				case "nt_mostlysunny": return "Mostly Clear";
				case "nt_rain": return "Rain";
				case "nt_sleet": return "Sleet";
				case "nt_snow": return "Snow";
				case "nt_sunny": return "Clear";
				case "nt_tstorms": return "Thunderstorms";
				default:
					Logger.Debug("No match for icon named [" + icon + "]");
					return "Unknown";
			}
		}

		//
		// This is the list of features that can be queried for. Use
		// FEATURES.conditions.ToString() to get the name, maybe.
		// Not all features are available at all subscription levels.
		private enum FEATURES {
			geolookup,
			conditions,
			forecast,
			astronomy,
			radar,
			satellite,
			webcams,
			history,
			alerts,
			hourly,
			hourly7day,
			forecast7day,
			yesterday,
			planner,
			autocomplete,
			almanac
		}
	}


	//
	// Class to hold current weather data
	internal class WeatherData {
		internal string ForecastLoc { get; set; }
		internal string Location {get; set;}
		internal string LocationElevation {get; set;}
		internal string LocationNeighborhood {get; set;}
		internal string LocationCity {get; set;}
		internal string LocationState {get; set;}
		internal string LocationZip {get; set;}
		internal double Latitude { get; set; }
		internal double Longitude { get; set; }
		internal string Elevation {get; set;}
		internal string Station {get; set;}
		internal string PressureTrending { get; set; }
		internal string LastUpdate {get; set;}
		internal double Temperature {get; set;}
		internal double Temperature_c {get; set;}
		internal double Humidity {get; set;}
		internal double WindSpeed {get; set;}
		internal double WindGust {get; set;}
		internal string WindDirection {get; set;}
		internal int WindDegrees {get; set;}
		internal double Dewpoint {get; set;}
		internal double Dewpoint_c {get; set;}
		internal double Precipitation {get; set;}
		internal double Precipitation_cm {get; set;}
		internal double HeatIndex_f {get; set;}
		internal double HeatIndex_c {get; set;}
		internal double WindChill_f {get; set;}
		internal double WindChill_c {get; set;}
		internal double SolarRadiation {get; set;}
		internal double UV {get; set;}
		internal double Visibility_mi { get; set; }
		internal double Visibility_km { get; set; }
		internal string TemperatureString {get; set;}
		internal string PressureString {get; set;}
		internal string DewpointString {get; set;}
		internal string HeatIndexString {get; set;}
		internal string WindChillString {get; set;}
		internal string Credit { get; set; }
		internal string CreditURL { get; set; }
		internal string MoonAge { get; set; }
		internal string MoonLight { get; set; }
		internal string Sunset { get; set; }
		internal string Sunrise { get; set; }
		internal double ApparentTemp_f { get; set; }
		internal double ApparentTemp_c { get; set; }
		internal double m_pressure_in;
		internal double m_pressure_mb;
		internal double m_old_pressure_in;
		internal double m_old_pressure_mb;
		internal string Weather { get; set; }
		internal string StationID { get; set; }
		internal string StationType { get; set; }
		internal int ForecastDays { get; set; }
		private Boolean metric;

		internal WeatherData(bool units) {
			// Initialize data structure
			metric = units;
			m_pressure_in = 0;
			m_pressure_mb = 0;
			m_old_pressure_in = 0;
			m_old_pressure_mb = 0;
			ForecastDays = 0;
		}

		internal double GetVisibility {
			get {
				if (metric) {
					return Visibility_km;
				} else {
					return Visibility_mi;
				}
			}
		}
		internal double GetPressure {
			get {
				if (metric) {
					return m_pressure_mb;
				} else {
					return m_pressure_in;
				}
			}
		}

		internal double PressureMB {
			set {
				m_old_pressure_mb = m_pressure_mb;
				m_pressure_mb = value;
			}
		}

		internal double PressureIN {
			set {
				m_old_pressure_in = m_pressure_in;
				m_pressure_in = value;
			}
		}


		internal string PressureTrend {
			get {
				// TODO: This should check multiple pressure readings, not just the last
				// one. There should be an array of readings.
				if (metric) {
					if (m_old_pressure_mb > 0) {
						if (m_old_pressure_mb < m_pressure_mb) {
							return "raising";
						} else if (m_old_pressure_mb > m_pressure_mb) {
							return "falling";
						} else {
							return "steady";
						}
					} else {
						return "N/A";
					}
				} else {
					if (m_old_pressure_in > 0) {
						if (m_old_pressure_in < m_pressure_in) {
							return "raising";
						} else if (m_old_pressure_in > m_pressure_in) {
							return "falling";
						} else {
							return "steady";
						}
					} else {
						return "N/A";
					}
				}
			}
		}

		internal string PressureUnits {
			get {
				if (metric) {
					return "Millibars";
				} else {
					return "Inches";
				}
			}
		}


		internal double GetTemperature() {
			if (metric) {
				return Temperature_c;
			} else {
				return Temperature;
			}
		}

		internal double GetHeatIndex() {
			if (metric) {
				return HeatIndex_c;
			} else {
				return HeatIndex_f;
			}
		}

		internal double GetWindChill() {
			if (metric) {
				return WindChill_c;
			} else {
				return WindChill_f;
			}
		}

		internal double GetDewpoint() {
			if (metric) {
				return Dewpoint_c;
			} else {
				return Dewpoint;
			}
		}

		internal double GetPrecipitation() {
			if (metric) {
				return Precipitation_cm;
			} else {
				return Precipitation;
			}
		}

		internal double GetApparentTemperature() {
			if (metric) {
				return ApparentTemp_c;
			} else {
				return ApparentTemp_f;
			}
		}
	}

	//
	// The Forecast class holds one period of forecast
	// data.  A period is typically 1 day.
	//
	internal class Forecast {
		internal string DateText { get; set; }
		internal string WeekDay { get; set; }
		internal int Year { get; set; }
		internal int Month { get; set; }
		internal int Day { get; set; }
		internal int Pop { get; set; }
		internal double High_c { get; set; }
		internal double High_f { get; set; }
		internal double Low_c { get; set; }
		internal double Low_f { get; set; }
		internal double AvgHumidity { get; set; }
		internal double MaxHumidity { get; set; }
		internal double MinHumidity { get; set; }
		internal double WindMax_mph { get; set; }
		internal double WindMax_kph { get; set; }
		internal double WindMaxDegrees { get; set; }
		internal string WindMaxDirection { get; set; }
		internal double WindAvg_mph { get; set; }
		internal double WindAvg_kph { get; set; }
		internal double WindAvgDegrees { get; set; }
		internal string WindAvgDirection { get; set; }
		internal string Condition { get; set; }
		internal string Icon { get; set; }
		internal string SkyIcon { get; set; }
		internal string IconURL { get; set; }
		internal string IconURLSmiley { get; set; }
		internal string IconURLGeneric { get; set; }
		internal string IconURLOldSchool { get; set; }
		internal string IconURLCartoon { get; set; }
		internal string IconURLMobile { get; set; }
		internal string IconURLSimple { get; set; }
		internal string IconURLContemporary { get; set; }
		internal string IconURLHelen { get; set; }
		internal string IconURLIncredible { get; set; }
		internal string DayForecastTitle { get; set; }
		internal string DayForecastText { get; set; }
		internal string NightForecastTitle { get; set; }
		internal string NightForecastText { get; set; }
		internal string NightCondition { get; set; }
		internal string Sunset { get; set; }
		internal string Sunrise { get; set; }
		internal string LastUpdate {get; set;}
		private bool metric;
		private string night_icon;

		internal Forecast(bool use_metric) {
			metric = use_metric;
			DateText = "N/A";
			WeekDay = "N/A";
			Year = DateTime.Now.Year;
			Month = DateTime.Now.Month;
			Day = DateTime.Now.Day;
			Condition = "N/A";
			Icon = "N/A";
			SkyIcon = "N/A";
			night_icon = "N/A";
			High_f = 0.0;
			High_c = 0.0;
			Low_f = 0.0;
			Low_c = 0.0;
			AvgHumidity = 0.0;
			MinHumidity = 0.0;
			MaxHumidity = 0.0;
			WindAvg_kph = 0.0;
			WindAvg_mph = 0.0;
			WindAvgDegrees = 0.0;
			WindAvgDirection = "N/A";
			WindMax_kph = 0.0;
			WindMax_mph = 0.0;
			WindMaxDegrees = 0.0;
			WindMaxDirection = "N/A";
			DayForecastTitle = "N/A";
			DayForecastText = "N/A";
			NightForecastTitle = "N/A";
			NightForecastText = "N/A";
			NightCondition = "N/A";
			Sunrise = "N/A";
			Sunset = "N/A";
			IconURL = "N/A";
			IconURLSmiley = "Invalid";
			IconURLGeneric = "Invalid";
			IconURLOldSchool = "Invalid";
			IconURLCartoon = "Invalid";
			IconURLMobile = "Invalid";
			IconURLSimple = "Invalid";
			IconURLContemporary = "Invalid";
			IconURLHelen = "Invalid";
			IconURLIncredible = "Invalid";
		}

		internal string NightIcon {
			get {
				return night_icon;
			}
			set {
				if (value.StartsWith("nt_")) {
					night_icon = value;
				} else {
					night_icon = "nt_" + value;
				}
			}
		}

		internal double GetHigh() {
			if (metric) {
				return High_c;
			} else {
				return High_f;
			}
		}

		internal double GetLow() {
			if (metric) {
				return Low_c;
			} else {
				return Low_f;
			}
		}

		internal double GetWindGust() {
			if (metric) {
				return WindMax_kph;
			} else {
				return WindMax_mph;
			}
		}

		internal double GetWindSpeed() {
			if (metric) {
				return WindAvg_kph;
			} else {
				return WindAvg_mph;
			}
		}

	}


	#region "SolarInfo sunrise/sunset calculations"
	//
	// SolarInfo
	// 
	//  Calculate sunrise and sunset times for a specific latitude and
	//  longitude.
	//
	// Acknowledgements:
	//   Based on a 'C' program by Robert Bond
	//
	//   The GST algorithms are from Sky and Telescope, June 1984 by
	//   Roger W. Sinnott
	//
	//   Adapted from algorithms presented in "Pratical Astronomy With Your 
	//   Calculator" by Peter Duffet-Smith
	//   

	internal class SolarInfo {
		internal DateTime Sunrise { get; private set; }
		internal DateTime Sunset { get; private set; }
		internal static ILogger Logger { get; set; }

		private SolarInfo() { }

		internal static SolarInfo ForDate(double latitude,
				double longitude, DateTime date) {

			TimeZone localzone = TimeZone.CurrentTimeZone;
			double JDE = 2444238.5;   // Julian date of EPOCH
			SolarInfo info = new SolarInfo();
			int year = date.Year;
			int month = date.Month;
			int day = date.Day;
			int tzl;

			double jd = JulianDate(month, day, year);
			double ed = jd - JDE;

			double lambda1 = solar_lon(ed);
			double lambda2 = solar_lon(ed + 1.0);

			double alpha1;
			double alpha2;
			double delta1;
			double delta2;

			// For some reason, this code thinks that west longitudes should
			// be positive, not negative. Reverse the sign.
			longitude *= -1.0;

			// Again, the code seems to think that the timezone offset
			// is backwards I.E. PST is 8 hrs, not -8 hrs from GMT.
			tzl = localzone.GetUtcOffset(date).Hours * -1;

			alpha1 = atan_q_deg((Math.Sin(deg2rad(lambda1))) *
					Math.Cos(deg2rad(23.441884)),
					Math.Cos(deg2rad(lambda1))) / 15.0;

			delta1 = asin_deg(Math.Sin(deg2rad(23.441884)) *
					Math.Sin(deg2rad(lambda1)));

			alpha2 = atan_q_deg((Math.Sin(deg2rad(lambda2))) *
					Math.Cos(deg2rad(23.441884)),
					Math.Cos(deg2rad(lambda2))) / 15.0;

			delta2 = asin_deg(Math.Sin(deg2rad(23.441884)) *
					Math.Sin(deg2rad(lambda2)));

			//Logger.Debug("Right ascension, declination for lon " + lambda1.ToString() + "is " + alpha1.ToString() + ", " + delta1.ToString());
			//Logger.Debug("Right ascension, declination for lon " + lambda2.ToString() + "is " + alpha2.ToString() + ", " + delta2.ToString());


			double st1r = rise(alpha1, delta1, latitude);
			double st1s = set(alpha1, delta1, latitude);
			double st2r = rise(alpha2, delta2, latitude);
			double st2s = set(alpha2, delta2, latitude);

			double m1 = adj24(gmst(jd - 0.5, 0.5 + tzl / 24.0) - longitude / 15);

			//Logger.Debug("local sidreal time of midnight is " + m1.ToString() + "  lon = " + longitude.ToString());

			double hsm = adj24(st1r - m1);
			//Logger.Debug("about " + hsm.ToString() + " hourse from midnight to dawn");

			double ratio = hsm / 24.07;
			//Logger.Debug(ratio.ToString() + " is how far dawn is into the day");

			if (Math.Abs(st2r - st1r) > 1.0) {
				st2r += 24.0;
				//Logger.Debug("st2r corrected from " + (st2r-24.0).ToString() + " to " + st2r.ToString());
			}

			double trise = adj24((1.0 - ratio) * st1r + ratio * st2r);

			hsm = adj24(st1s - m1);
			//Logger.Debug("about " + hsm.ToString() + " hours from midnight to sunset");
			ratio = hsm / 24.07;
			//Logger.Debug(ratio.ToString() + " is ho far sunset is into the day");

			if (Math.Abs(st2s - st1s) > 1.0) {
				st2s += 24.0;
				//Logger.Debug("st2s corrected from " + (st2s-24.0).ToString() + " to " + st2s.ToString());
			}

			double tset = adj24((1.0 - ratio) * st1s + ratio * st2s);
			//Logger.Debug("Uncorrected rise = " + trise.ToString() + ", set = " + tset.ToString());

			//$ar = $a1r * 360.0 / (360.0 + $a1r - $a2r);
			//$as = $a1s * 360.0 / (360.0 + $a1s - $a2s);

			double delta = (delta1 + delta2) / 2.0;
			double tri = acos_deg(sin_deg(latitude) / cos_deg(delta));

			double x = 0.835608;      // correction for refraction, parallax
			double y = asin_deg(sin_deg(x) / sin_deg(tri));
			// $da = &asin_deg(&tan_deg($x)/&tan_deg($tri));
			double dt = 240.0 * y / cos_deg(delta) / 3600;
			//Logger.Debug("Corrections: dt = " + dt.ToString());

			info.Sunrise = date.Date.AddMinutes(
					lst_to_hm(trise - dt, jd, tzl, longitude, year));

			info.Sunset = date.Date.AddMinutes(
					lst_to_hm(tset + dt, jd, tzl, longitude, year));

			return info;
		}  // end of ForDate

		//
		// rtod
		//
		//  radains to degrees conversion

		private static double rtod(double deg) {
			return (deg * 180.0 / Math.PI);
		}

		//
		// adj360
		//
		//  convert to number between 0 and 360

		private static double adj360(double deg) {

			while (deg < 0.0) {
				deg += 360.0;
			}
			while (deg > 360.0) {
				deg -= 360.0;
			}
			return deg;
		}

		//
		// adj24
		//
		//  convert to a number between 0 and 24

		private static double adj24(double hrs) {

			while (hrs < 0.0) {
				hrs += 24.0;
			}
			while (hrs > 24.0) {
				hrs -= 24.0;
			}

			return hrs;
		}


		//
		// JulianDate
		//
		//  Given a month, day, year, calculate the julian date and return
		//  it.

		private static double JulianDate(int m, int d, int y) {
			int a;
			int b;
			double jd;

			if ((m == 1) || (m == 2)) {
				y--;
				m += 12;
			}

			// Can't handle dates before 1583
			if (y < 1583) {
				return 0;
			}

			a = (int)(y / 100);
			b = (int)(2 - a + a / 4);
			b += (int)(y * 365.25);
			b += (int)((30.6001 * (m + 1.0)));
			jd = (double)d + (double)b + 1720994.5;
			//Logger.Debug("Julian date for " + m.ToString() + "/" + d.ToString() + "/" + y.ToString() + " is " + jd.ToString());

			return jd;
		}


		//
		// solar_lon
		//
		//  ???

		private static double solar_lon(double ed) {
			double n;
			double m;
			double e;
			double ect;
			double errt;
			double v;

			n = 360.0 * ed / 365.2422;
			n = adj360(n);
			m = n + 278.83354 - 282.596403;
			m = adj360(m);
			m = deg2rad(m);
			e = m;
			ect = 0.016718;

			while ((errt = e - ect * Math.Sin(e) - m) > 0.0000001) {
				e = e - errt / (1 - ect * Math.Cos(e));
			}

			v = 2 * Math.Atan(1.0168601 * Math.Tan(e / 2));
			v = adj360(((v * 180.0) / Math.PI) + 282.596403);
			//Logger.Debug("Solar Longitude for " + ed.ToString() + " days is " + v.ToString());

			return v;
		}


		//
		// acos_deg
		//
		//  returns the arc cosin in degrees

		private static double acos_deg(double x) {
			return rtod(Math.Acos(x));
		}


		//
		// asin_deg
		//
		//  returns the arc sin in degrees

		private static double asin_deg(double x) {
			return rtod(Math.Asin(x));
		}


		//
		// atan_q_deg
		//
		//  returns the arc tangent in degrees and does what to it?

		private static double atan_q_deg(double y, double x) {
			double rv;

			if (y == 0) {
				rv = 0;
			} else if (x == 0) {
				rv = (y > 0) ? 90.0 : -90.0;
			} else {
				rv = atan_deg(y / x);
			}

			if (x < 0) {
				return rv + 180.0;
			}

			if (y < 0) {
				return rv + 360.0;
			}

			return rv;
		}


		//
		// atan_q_deg
		//
		//  returns the arc tangent in degrees

		private static double atan_deg(double x) {
			return rtod(Math.Atan(x));
		}

		//
		// sin_deg
		//
		//  returns the sin in degrees

		private static double sin_deg(double x) {
			return Math.Sin(deg2rad(x));
		}


		//
		// cos_deg
		//
		//  returns the cos in degrees

		private static double cos_deg(double x) {
			return Math.Cos(deg2rad(x));
		}


		// 
		// tan_deg
		//
		//  returns the tangent in degrees

		private static double tan_deg(double x) {
			return Math.Tan(deg2rad(x));
		}


		//
		// rise_set
		//
		//  Calculates the uncorrected sun rise and set times

		private static double rise(double alpha, double delta, double lat) {

			double tar = sin_deg(delta) / cos_deg(lat);

			if ((tar < -1.0) || (tar > 1.0)) {
				return 0.0;
			}

			double h = acos_deg(-tan_deg(lat) * tan_deg(delta)) / 15.0;
			double lstr = 24.0 + alpha - h;
			if (lstr > 24.0) {
				lstr -= 24.0;
			}

			return lstr;
		}

		private static double set(double alpha, double delta, double lat) {

			double tar = sin_deg(delta) / cos_deg(lat);

			if ((tar < -1.0) || (tar > 1.0)) {
				return 0.0;
			}

			double h = acos_deg(-tan_deg(lat) * tan_deg(delta)) / 15.0;
			double lsts = alpha + h;
			if (lsts > 24.0) {
				lsts -= 24.0;
			}

			return lsts;
		}


		//
		// lst_to_hm
		//
		//  converts a time value and juilan date to minutes past midnight

		private static int lst_to_hm(double lst, double jd, double tzl,
				double lon, int yr) {

			double gst = lst + lon / 15.0;

			if (gst > 24.0) {
				gst -= 24.0;
			}

			double jzjd = JulianDate(1, 0, yr);
			double ed = jd - jzjd;
			double t = (jzjd - 2415020.0) / 36525.0;
			double r = 6.6460656 + 2400.05126 * t + 2.58E-05 * t * t;
			double b = 24.0 - (r - 24.0 * (yr - 1900));
			double t0 = ed * 0.0657098 - b;

			if (t0 < 0.0) {
				t0 += 24;
			}
			double gmt = gst - t0;

			if (gmt < 0) {
				gmt += 24.0;
			}

			gmt = gmt * 0.99727 - tzl;
			if (gmt < 0) {
				gmt += 24.0;
			}

			// gmt is decimal hours past midnight. The DateTime
			// AddMinutes() method needs minutes.  Convert gmt
			// to minutes and return.

			return (int)((gmt * 60) + 0.5);
		}


		//
		// gmst
		//
		//  calculates some kind of time value ???

		private static double gmst(double j, double f) {

			double d = j - 2451545.0;
			double t = d / 36525.0;
			double t1 = Math.Floor(t);
			double j0 = t1 * 36525.0 + 2451545.0;
			double t2 = (j - j0 + 0.5) / 36525.0;
			double s = 24110.54841 + 184.812866 * t1;
			s += 8640184.812866 * t2;
			s += 0.093104 * t * t;
			s -= 0.0000062 * t * t * t;
			s /= 86400.0;
			s -= Math.Floor(s);
			s = 24 * (s + (f - 0.5) * 1.002737909);
			if (s < 0) {
				s += 24.0;
			}
			if (s > 24.0) {
				s -= 24.0;
			}

			//Logger.Debug("For jd = " + j.ToString() + ", f = " + f.ToString() + " , gst = " + s.ToString());
			return s;
		}


		//
		// deg2rad
		//
		//  converts degrees to radians

		private static double deg2rad(double deg) {
			return deg * Math.PI / 180.0;
		}
	} // End of SolarInfo class
	#endregion
}
