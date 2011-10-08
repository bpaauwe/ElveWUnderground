//
//----------------------------------------------------------------------------
//
// ElveWUnderground
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
// current condition data at a user defined interval from a user defined
// weather station (personal weather station).  Forecast data is queried
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
//	Precipiation            Precipiation for today
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
//	ConditionIconURLs       Array of URL's pointing to the condition icon
//  ---  URL's to the various icon sets supported by Weather Underground
//  SmileyConditionIconURLs
//  GenericConditionIconURLs
//  OldSchoolConditionIconURLs
//  CartoonConditionIconURLs
//  MobileConditionIconURLs
//  SimpleConditionIconURLs
//  ContemporaryConditionIconURLs
//  HelenConditionIconURLs
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

namespace ElveWUnderground {

	[Driver(
			"Weather Underground",
			"This driver pulls weather data from the Weather Underground based " +
			"on a personal weather station ID. The station ID can be found at " +
			"http://www.wunderground.com/weatherstation/index.asp.",
			"Robert Paauwe",
			"Weather",
			"",
			"weather",
			DriverCommunicationPort.Network,
			DriverMultipleInstances.MultiplePerDriverService,
			1, // Major version
			0, // Minor version
			DriverReleaseStages.Production,
			"Weather Underground, Inc.",
			"http://www.wunderground.com/",
			null
			)]
	public class ElveWUndergroundDriver : Driver, IWeatherDriver {
		private System.Timers.Timer m_poll_timer;
		private System.Timers.Timer m_fcast_timer;
		private string m_station_id;
		private int m_device_poll;
		private bool m_metric;
		private WeatherData m_weather;
		private Forecast[] m_forecasts = new Forecast[6]; // Currently only 6 days of info.

		//
		// Driver user configuration settings
		//
		// Weather station identification string
		// Units (english or metric)
		// Polling interval
		//
		[DriverSettingAttribute("Station Identifier",
				"The weather station identifier to query. Search for station ID's at " +
				"http://www.wunderground.com/weatherstation/index.asp",
				null, true)]
		public string StationIDSetting {
			set {
				m_station_id = value;
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
				1, 3600, "300", true)]
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

			Logger.Info("Weather Underground Driver version " +
				System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString() +
				" starting.");

			// Allocate data classes
			for (int i = 0; i < m_forecasts.Length; i++) {
				m_forecasts[i] = new Forecast(m_metric);
			}
			m_weather = new WeatherData(m_metric);
			
			// Attempt connection to the Weather Underground server and pull current values
			if (ReadWeatherData(m_station_id)) {
				ApparentTemp();
			}

			// This should only be called twice a day.
			ReadWeatherForecast(m_weather.ForecastLoc);

			// Start a timer to pull data at polling frequency.
			m_poll_timer = new System.Timers.Timer();
			m_poll_timer.Elapsed += new ElapsedEventHandler(PollWUnderground);
			m_poll_timer.Interval = m_device_poll * 1000;
			m_poll_timer.Enabled = true;

			// Start a timer to pull forecast data at hourly frequency.
			m_fcast_timer = new System.Timers.Timer();
			m_fcast_timer.Elapsed += new ElapsedEventHandler(PollWUndergroundForecast);
			m_fcast_timer.Interval = 3600000;
			m_fcast_timer.Enabled = true;

			return true;
		}

		public override void StopDriver() {
			Logger.Info("Weather Underground Driver finished.");
			m_poll_timer.Enabled = false;
			m_poll_timer.Dispose();

			m_fcast_timer.Enabled = false;
			m_fcast_timer.Dispose();
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
				return new ScriptString(m_weather.PressureTrend);
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
				throw new NotSupportedException();
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

        [ScriptObjectPropertyAttribute("Day Icon", "Gets an array of condition icons id by day.",
			"the {NAME} condition icon id for day {INDEX|0}", null)]
        [SupportsDriverPropertyBinding]
        public IScriptArray DayIconIDs {
            get {
                // return a 0 based array of day icon ids
				return new ScriptArrayMarshalByValue(m_forecasts.Select(f => IconID(f.Icon).ToString()), 0);
            }
        }

        [ScriptObjectPropertyAttribute("Night Icon", "Gets an array of night time condition icons id by day.",
			"the {NAME} night time condition icon id for day {INDEX|0}", null)]
        [SupportsDriverPropertyBinding]
        public IScriptArray NightIconIDs {
            get {
                // return a 0 based array of night icon ids
				return new ScriptArrayMarshalByValue(m_forecasts.Select(f => IconID(f.NightIcon).ToString()), 0);
            }
        }

        [ScriptObjectPropertyAttribute("Condition Icon Urls", "Gets an array of condition icons urls by day.",
			"the {NAME} condition icon url for day {INDEX|0}", null)]
        [SupportsDriverPropertyBinding]
        public IScriptArray ConditionIconUrls {
            get {
                // return a 0 based array of condition icon urls
				return new ScriptArrayMarshalByValue(m_forecasts.Select(f => f.IconURL), 0);
            }
        }

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
        public ScriptString Sunrise {
            get {
				return new ScriptString(m_weather.Sunrise);
            }
        }

        [ScriptObjectPropertyAttribute("Sunset", "Gets the current sunset time.",
			"the {NAME} sunset", null)]
        [SupportsDriverPropertyBinding]
        public ScriptString Sunset {
            get {
				return new ScriptString(m_weather.Sunset);
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
				return double.Parse(str);
			} catch {
				return 0.0;
			}
		}

		//
		// Pull data from the Weather Underground web site and parse
		// the returned XML.
		//
		private bool ReadWeatherData(string station) {
			WeatherData wd = new WeatherData(m_metric);
			XmlDocument xml = new XmlDocument();
			string url;
			XmlNode node;

			Logger.Debug("Read data from web site.");

			url = "http://api.wunderground.com/weatherstation/WXCurrentObXML.asp?ID=" +
				station;

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
				node = xml.ChildNodes[1];

				foreach (XmlNode n in node.ChildNodes) {
					Logger.Debug(" -> " + n.Name + "  : " + n.InnerText);
					switch (n.Name) {
						case "credit": m_weather.Credit = n.InnerText; break;
						case "credit_URL": m_weather.CreditURL = n.InnerText; break;
						case "location":
							// Location has multiple child nodes!
							m_weather.Location = n.SelectNodes(".//full")[0].InnerText;
							m_weather.LocationCity = n.SelectNodes(".//city")[0].InnerText;
							m_weather.LocationState = n.SelectNodes(".//state")[0].InnerText;
							m_weather.LocationNeighborhood = n.SelectNodes(".//neighborhood")[0].InnerText;
							m_weather.LocationZip = n.SelectNodes(".//zip")[0].InnerText;

							try {
								m_weather.ForecastLoc = n.SelectNodes(".//latitude")[0].InnerText;
								m_weather.ForecastLoc += "," + n.SelectNodes(".//longitude")[0].InnerText;
							} catch (Exception ex1) {
								Logger.Error("Failed to parse location: " + ex1.Message);
								m_weather.ForecastLoc = "";
							}
							break;
						case "observation_time": m_weather.LastUpdate = n.InnerText; break;
						case "temperature_string": m_weather.TemperatureString = n.InnerText; break;
						case "temp_f": m_weather.Temperature = DParse(n.InnerText); break;
						case "temp_c": m_weather.Temperature_c = DParse(n.InnerText); break;
						case "relative_humidity": m_weather.Humidity = DParse(n.InnerText); break;
						case "wind_string": break;
						case "wind_dir": m_weather.WindDirection = n.InnerText; break;
						case "wind_degrees": m_weather.WindDegrees = int.Parse(n.InnerText); break;
						case "wind_mph": m_weather.WindSpeed = DParse(n.InnerText); break;
						case "wind_gust_mph": m_weather.WindGust = DParse(n.InnerText); break;
						case "pressure_string": m_weather.PressureString = n.InnerText; break;
						case "pressure_in": m_weather.PressureIN = DParse(n.InnerText); break;
						case "pressure_mb": m_weather.PressureMB = DParse(n.InnerText); break;
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
						case "heat_index_f": m_weather.HeatIndex_f = DParse(n.InnerText);  break;
						case "heat_index_c": m_weather.HeatIndex_c = DParse(n.InnerText);  break;
						case "heat_index_string": m_weather.HeatIndexString = n.InnerText;  break;
						case "windchill_f": m_weather.WindChill_c = DParse(n.InnerText);  break;
						case "windchill_c": m_weather.WindChill_c = DParse(n.InnerText);  break;
						case "windchill_string": m_weather.WindChillString = n.InnerText;  break;
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


		//
		// Query for forecast data.  How often should we do this query?
		//
		private bool ReadWeatherForecast(string location) {
			XmlDocument xml = new XmlDocument();
			XmlNode node;
			XmlNodeList nl;
			XmlNodeList tag;
			string url;
			int period;

			url = "http://api.wunderground.com/auto/wui/geo/ForecastXML/index.xml?query=" +
				location;

			Logger.Debug("Query for forecast using: " + url);

			try {
				xml.Load(url);
			} catch (Exception ex) {
				Logger.Error("Error " + ex.Message + " while loading " + url);
				return false;
			}


			// Parse forecast XML
			try {
				node = xml.ChildNodes[1];

				//
				// Expect 3 children
				//   txt_forecast
				//   simpleforecast
				//   moon_phase
				foreach (XmlNode n in node.ChildNodes) {
					Logger.Info("  -> " + n.Name + " = " + n.InnerText);
					if (n.Name == "simpleforecast") {
						// The simple forecast section provides a daily forecast for
						// 6 days (periods), including today. It does not include
						// text descriptions or nighttime forecasts.
						//
						// The period will map directly to the forecast array index
						//    index = period - 1;
						nl = n.SelectNodes(".//forecastday");
						foreach (XmlNode f in nl) {
							Logger.Debug(f.Name + " -- " + f.InnerXml);

							f.CreateNavigator();

							try {
								// Period defines which slot in the forecast array this is for.
								period = int.Parse(f.SelectNodes(".//period")[0].InnerText);
								//Logger.Debug("   -> period = " + period.ToString());

								// Date
								try {
									m_forecasts[period - 1].DateText =
										f.SelectNodes(".//date/monthname")[0].InnerText + " " +
										f.SelectNodes(".//date/day")[0].InnerText + ", " +
										f.SelectNodes(".//date/year")[0].InnerText;

									m_forecasts[period - 1].Year = int.Parse(f.SelectNodes(".//date/year")[0].InnerText);
									m_forecasts[period - 1].Month = int.Parse(f.SelectNodes(".//date/month")[0].InnerText);
									m_forecasts[period - 1].Day = int.Parse(f.SelectNodes(".//date/day")[0].InnerText);
									
									m_forecasts[period - 1].WeekDay = f.SelectNodes(".//date/weekday")[0].InnerText;
								} catch (Exception ex) {
									Logger.Error("Failed to parse date:" + ex.Message);
								}

								// High temps
								try {
									m_forecasts[period-1].High_f = DParse(f.SelectNodes(".//high/fahrenheit")[0].InnerText);
									m_forecasts[period-1].High_c = DParse(f.SelectNodes(".//high/celsius")[0].InnerText);
								} catch (Exception ex) {
									Logger.Error("Failed to parse high temps:" + ex.Message);
								}

								// Low temps
								try {
									m_forecasts[period-1].Low_f = DParse(f.SelectNodes(".//low/fahrenheit")[0].InnerText);
									m_forecasts[period-1].Low_c = DParse(f.SelectNodes(".//low/celsius")[0].InnerText);
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

								// icon URL. Currently using default
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
								} catch (Exception ex) {
									Logger.Error("Failed to parse icon url:" + ex.Message);
								}

								tag = f.SelectNodes(".//pop");
								//Logger.Info("   -> pop = " + tag[0].InnerText);

								m_forecasts[period - 1].LastUpdate = DateTime.Now.ToString();
							} catch {
								Logger.Info("Failed to parse the forecast period.");
							}
						}

					} else if (n.Name == "txt_forecast") {
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

						nl = n.SelectNodes(".//forecastday");
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
								index++;
							} else {
								m_forecasts[index].DayForecastText = text;
								m_forecasts[index].DayForecastTitle = title;
							}
						}
					} else if (n.Name == "moon_phase") {
						// Moon phase info:
						//  Percent Illuminated
						//  Age of Moon
						//  Time
						//  Sunset
						//  Sunrise
						try {
							m_weather.MoonAge = n.SelectNodes(".//ageOfMoon")[0].InnerText;
							m_weather.MoonLight = n.SelectNodes(".//percentIlluminated")[0].InnerText;
							m_weather.Sunset = n.SelectNodes(".//sunset/hour")[0].InnerText + ":" +
								n.SelectNodes(".//sunset/minute")[0].InnerText;
							m_weather.Sunrise = n.SelectNodes(".//sunrise/hour")[0].InnerText + ":" +
								n.SelectNodes(".//sunrise/minute")[0].InnerText;
						} catch (Exception ex) {
							Logger.Error("Moon phase parsing failed: " + ex.Message);
						}
					}
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

			if (ReadWeatherData(m_station_id)) {
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
				DevicePropertyChangeNotification("Precipiation", m_weather.GetPrecipitation());
				DevicePropertyChangeNotification("SolarRadiation", m_weather.SolarRadiation);
				DevicePropertyChangeNotification("Sunset", m_weather.Sunset);
				DevicePropertyChangeNotification("Sunrise", m_weather.Sunrise);
				DevicePropertyChangeNotification("Temperature", m_weather.GetTemperature());
				DevicePropertyChangeNotification("UVIndex", m_weather.UV);
				DevicePropertyChangeNotification("WindDirectionDegrees", m_weather.WindDegrees);
				DevicePropertyChangeNotification("WindDirectionText", m_weather.WindDirection);
				DevicePropertyChangeNotification("WindGustSpeed", m_weather.WindGust);
				DevicePropertyChangeNotification("WindSpeed", m_weather.WindSpeed);
				DevicePropertyChangeNotification("Windchill", m_weather.GetWindChill());
				DevicePropertyChangeNotification("ApparentTemperature", m_weather.GetApparentTemperature());
			}
		}

		//
		// Query WUnderground for forecast infomation and update the 
		// forecast properties.
		//
		private void PollWUndergroundForecast(Object sender, EventArgs e) {

			Logger.Debug("Retrieve Forecast data.");

			if (ReadWeatherForecast(m_weather.ForecastLoc)) {

				for (int i = 0; i < m_forecasts.Length; i++) {
					DevicePropertyChangeNotification("Highs", m_forecasts[i].GetHigh());
					DevicePropertyChangeNotification("Lows", m_forecasts[i].GetLow());
					DevicePropertyChangeNotification("Conditions", m_forecasts[i].Condition);
					DevicePropertyChangeNotification("Dates",
						new DateTime(m_forecasts[i].Year, m_forecasts[i].Month, m_forecasts[i].Day));
					DevicePropertyChangeNotification("DatesText", m_forecasts[i].DateText);
					DevicePropertyChangeNotification("WeekDayTexts", m_forecasts[i].WeekDay);
					DevicePropertyChangeNotification("DayIconIDs", IconID(m_forecasts[i].Icon));
					DevicePropertyChangeNotification("NightIconIDs", IconID(m_forecasts[i].NightIcon));
					DevicePropertyChangeNotification("ConditionIconURLs", m_forecasts[i].IconURL);
					DevicePropertyChangeNotification("SmileyConditionIconURLs", m_forecasts[i].IconURLSmiley);
					DevicePropertyChangeNotification("GenericConditionIconURLs", m_forecasts[i].IconURLGeneric);
					DevicePropertyChangeNotification("OldSchoolConditionIconURLs", m_forecasts[i].IconURLOldSchool);
					DevicePropertyChangeNotification("CartoonConditionIconURLs", m_forecasts[i].IconURLCartoon);
					DevicePropertyChangeNotification("MobileConditionIconURLs", m_forecasts[i].IconURLMobile);
					DevicePropertyChangeNotification("SimpleConditionIconURLs", m_forecasts[i].IconURLSimple);
					DevicePropertyChangeNotification("ContemporaryConditionIconURLs", m_forecasts[i].IconURLContemporary);
					DevicePropertyChangeNotification("HelenConditionIconURLs", m_forecasts[i].IconURLHelen);
					DevicePropertyChangeNotification("IncredibleConditionIconURLs", m_forecasts[i].IconURLIncredible);
					DevicePropertyChangeNotification("DayDescriptions", m_forecasts[i].DayForecastText);
					DevicePropertyChangeNotification("NightDescriptions", m_forecasts[i].NightForecastText);
				}

				// Not an array
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
		// clear = 32
		// cloudy = 28
		// flurries = 14
		// fog = 20
		// hazy = 21
		// mostlycloudy = 26
		// partlycloudy = 28
		// partlysunny = 30
		// mostlysunny = 34
		// rain = 18
		// sleet = 10
		// snow = 15
		// sunny = 32
		// tstorms = 17
		// unknown = 44
		internal int IconID(string icon) {
			switch (icon) {
				case "clear": return 32;
				case "flurries": return 14;
				case "fog": return 20;
				case "hazy": return 21;
				case "cloudy": return 26;
				case "mostlycloudy": return 26;
				case "partlycloudy": return 30;
				case "partlysunny": return 28;
				case "mostlysunny": return 34;
				case "rain": return 40;
				case "sleet": return 10;
				case "snow": return 41;
				case "sunny": return 32;
				case "tstorms": return 17;
				case "chanceflurries": return 15;
				case "chancerain": return 39;
				case "chancesleet": return 10;
				case "chancesnow": return 42;
				case "chancetstorms": return 17;
				case "unknown": return 44;
				case "nt_clear": return 31;
				case "nt_cloudy": return 27;
				case "nt_flurries": return 46;
				case "nt_fog": return 20;
				case "nt_hazy": return 21;
				case "nt_mostlycloudy": return 29;
				case "nt_partlycloudy": return 29;
				case "nt_partlysunny": return 29;
				case "nt_mostlysunny": return 29;
				case "nt_rain": return 45;
				case "nt_sleet": return 46;
				case "nt_snow": return 46;
				case "nt_sunny": return 31;
				case "nt_tstorms": return 47;
				default:
					Logger.Debug("No IconID match for icon named [" + icon + "]");
					return 44;
			}
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
		internal string Station {get; set;}
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
		internal Boolean metric {get; set;}
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

		internal WeatherData(bool units) {
			// Initialize data structure
			metric = units;
			m_pressure_in = 0;
			m_pressure_mb = 0;
			m_old_pressure_in = 0;
			m_old_pressure_mb = 0;
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

	internal class Forecast {
		internal string DateText { get; set; }
		internal string WeekDay { get; set; }
		internal int Year { get; set; }
		internal int Month { get; set; }
		internal int Day { get; set; }
		internal double High_c { get; set; }
		internal double High_f { get; set; }
		internal double Low_c { get; set; }
		internal double Low_f { get; set; }
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
		internal string LastUpdate {get; set;}
		private bool metric;
		private string night_icon;

		internal Forecast(bool use_metric) {
			metric = use_metric;
			DateText = "";
			WeekDay = "";
			Year = 0;
			Month = 0;
			Day = 0;
			Condition = "N/A";
			Icon = "N/A";
			SkyIcon = "N/A";
			night_icon = "N/A";
			High_f = 0.0;
			High_c = 0.0;
			Low_f = 0.0;
			Low_c = 0.0;
			DayForecastTitle = "";
			DayForecastText = "";
			NightForecastTitle = "";
			NightForecastText = "";
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

	}
}
