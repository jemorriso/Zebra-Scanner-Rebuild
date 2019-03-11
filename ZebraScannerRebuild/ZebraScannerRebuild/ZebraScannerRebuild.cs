using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using System.Threading;

using System.Text.RegularExpressions;

using Motorola.Snapi;
using Motorola.Snapi.Constants.Enums;
using Motorola.Snapi.Constants;
using Motorola.Snapi.EventArguments;

using Renci.SshNet;

using log4net;
using log4net.Config;
using System.Reflection;

namespace ZebraScannerRebuild
{
	public static class BarcodeScannerManagerExtension
	{
		public static IMotorolaBarcodeScanner GetScannerFromCradleId(this BarcodeScannerManager instance, uint scannerId)
		{
			foreach (var scanner in instance.GetDevices())
			{
				// barcodescanevent is triggered from cradle.
				// assumption: connected scanner has id (cradle id+1)
				if (scanner.Info.ScannerId == scannerId+1)
				{
					Console.WriteLine("found scanner, id: " + (scannerId+1));
					return scanner;
				}
			}
			return null;
		}
	}

	// define timer class that accepts scanner id and led mode so event handler has access to these fields
	class ScannerTimer : System.Timers.Timer
	{
		public uint scannerId;
		// needed for ledtimer, but not scantimer
		public LedMode? ledOff;
	}

	enum BarcodeType { nid, location, None };

	class ZebraScannerRebuild
	{
		private static ConnectionInfo ConnInfo;

		private static readonly ILog _log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);


		private static Dictionary<string, Tuple<LedMode?, LedMode?, int, BeepPattern?>> notifications = new Dictionary<string, Tuple<LedMode?, LedMode?, int, BeepPattern?>>();

		//private static System.Timers.Timer _scanTimer = new System.Timers.Timer(5000) { AutoReset = false };
		//private static System.Timers.Timer _ledTimer = new System.Timers.Timer(100) { AutoReset = false };

		// if there were multiple scanners, this should be a dictionary, since there will be multiple timers that may need to be stopped
		// timers stores with key cradleID
		private static Dictionary<uint, ScannerTimer> timers = new Dictionary<uint, ScannerTimer>();
		private static ScannerTimer _scanTimer;

		private static Tuple<string, BarcodeType> prevScan;

		public void Start()
		{
			// Setup logging
			XmlConfigurator.Configure();

			if (!(BarcodeScannerManager.Instance.Open()))
			{
				_log.Fatal("Failed to open CoreScanner driver");
			}
			else
			{

				_log.Debug("CoreScanner driver instance opened");
			}

			// Setup SSH connection info for remote inventory database access
			ConnInfo = new ConnectionInfo("jmorrison", "jmorrison",
				new AuthenticationMethod[] {
					// Password based Authentication
					new PasswordAuthenticationMethod("jmorrison","Pa$$wordjm")
				}
			);
			_log.Debug("Added SSH connection info: jmorrison@jmorrison");

			BarcodeScannerManager.Instance.RegisterForEvents(EventType.Barcode, EventType.Pnp);

			BarcodeScannerManager.Instance.DataReceived += OnDataReceived;
			BarcodeScannerManager.Instance.ScannerAttached += OnScannerAttached;
			BarcodeScannerManager.Instance.ScannerDetached += OnScannerDetached;

			_log.Debug("Subscribed for events in BarcodeScannerManager: CCoreScanner.Barcode, CCoreScanner.Pnp");
			_log.Debug("Subscribed for events in Main: BarcodeScannerManager.ScannerAttached, BarcodeScannerManager.ScannerDetached");


			notifications.Add("tryDatabase", Tuple.Create((LedMode?)LedMode.GreenOn, (LedMode?)LedMode.GreenOff, 1000, (BeepPattern?)null));
			notifications.Add("timerUp", Tuple.Create((LedMode?)LedMode.RedOn, (LedMode?)LedMode.RedOff, 50, (BeepPattern?)BeepPattern.TwoLowShort));
			notifications.Add("barcodeFailure", Tuple.Create((LedMode?)LedMode.RedOn, (LedMode?)LedMode.RedOff, 100, (BeepPattern?)BeepPattern.OneLowLong));


			List<IMotorolaBarcodeScanner> scannerList;
			scannerList = BarcodeScannerManager.Instance.GetDevices();
			Console.WriteLine("number of connected scanners: " + scannerList.Count);

			//***** need to call configureScanners here
			// must be applied to cradle, not host
			scannerList[0].SetHostMode(HostMode.USB_IBMHID);


			//var connectedScanner = scannerList[0];
			//Console.WriteLine("Scanner hostmode: " + connectedScanner.Info.UsbHostMode);
			//Console.WriteLine("Scanner manufactured: " + connectedScanner.Info.DateOfManufacture);
			//Console.WriteLine("Scanner PID: " + connectedScanner.Info.ProductId);
			//Console.WriteLine("Scanner model #: " + connectedScanner.Info.ModelNumber);
			//Console.WriteLine("Scanner ID #: " + connectedScanner.Info.ScannerId);

			//connectedScanner = scannerList[1];
			//Console.WriteLine("Scanner hostmode: " + connectedScanner.Info.UsbHostMode);
			//Console.WriteLine("Scanner manufactured: " + connectedScanner.Info.DateOfManufacture);
			//Console.WriteLine("Scanner PID: " + connectedScanner.Info.ProductId);
			//Console.WriteLine("Scanner model #: " + connectedScanner.Info.ModelNumber);
			//Console.WriteLine("Scanner ID #: " + connectedScanner.Info.ScannerId);



			//scannerList[0].Actions.SetAttribute(538, DataType.Bool, "True");
			//			< attribute >

			//  < id > 538 </ id >

			//  < datatype > F </ datatype >

			//  < value > True </ value >

			//</ attribute >
		}

		public void Stop()
		{
			_log.Debug("Zebra Scanner Service stopped");
			BarcodeScannerManager.Instance.Close();
		}
		// PnpEventArgs doesn't have scanner serial
		private static void OnScannerAttached(object sender, PnpEventArgs e)
		{
			_log.Debug("Scanner id=" + e.ScannerId + " attached");

			Console.WriteLine("Scanner id=" + e.ScannerId + " attached");
		}

		private static void OnScannerDetached(object sender, PnpEventArgs e)
		{
			_log.Debug("Scanner id=" + e.ScannerId + " attached");

			Console.WriteLine("Scanner id=" + e.ScannerId + " detached");
		}

		private static void OnDataReceived(object sender, BarcodeScanEventArgs e)
		{	// Modify logging depending on if in multipoint mode - can get better info from prefix
			Console.WriteLine("Barcode scan detected from scanner id=" + e.ScannerId + ": " + e.Data);
			_log.Debug("Barcode scan detected from scanner id=" + e.ScannerId + ": " + e.Data);

			// convert barcode to uppercase and strip any whitespace
			string barcode = e.Data.ToUpper().Trim();

			BarcodeType barcodeType = CheckBarcode(barcode);

			if (barcodeType == BarcodeType.None)
			{
				_log.Error("Barcode " + e.Data + " not recognized as location or NID");
				SendNotification(e.ScannerId, notifications["barcodeFailure"]);
			}
			else
			{
				// if successful scan, then either stop timer or restart start it, so stop here.
				// stopping timer avoids potential race condition
				if (_scanTimer != null)
				{
					_scanTimer.Stop();
				}
				//if (timers[e.ScannerId] != null)
				//{
				//	timers[e.ScannerId].Stop();
				//}

				_scanTimer = new ScannerTimer
				{
					Interval = 5000,
					AutoReset = false,
					scannerId = e.ScannerId,
					ledOff = null
				};
				_scanTimer.Elapsed += OnScanTimerElapsed;

				//timers[e.ScannerId] = new ScannerTimer
				//{
				//	Interval = 5000,
				//	AutoReset = false,
				//	scannerId = e.ScannerId,
				//	ledOff = null
				//};
				//timers[e.ScannerId].Elapsed += OnScanTimerElapsed;

				_log.Debug("Barcode " + barcode + " recognized as type " + barcodeType);
				Console.WriteLine("Barcode " + barcode + " recognized as type " + barcodeType);

				// case 1: prevScan: null		current: nid1 		-> prevScan: nid1		timer: start	()
				// case 2: prevScan: null		current: location1	-> prevScan: location1	timer: start	()	 
				// case 3: prevScan: nid1		current: nid1		-> prevScan: null		timer: stop		(remove nid's location from database)				
				// case 4: prevScan: nid1		current: nid2		-> prevScan: nid2		timer: start	(overwrite previous nid with new prevScan nid)
				// case 5: prevScan: nid1		current: location1	-> prevScan: location1	timer: start	(nid scanned first - overwrite with location)
				// case 6: prevScan: location1	current: location1	-> prevScan: location1	timer: start	(overwrite same location)
				// case 7: prevScan: location1	current: location2 	-> prevScan: location2	timer: start	(overwrite new location)
				// case 8: prevScan: location1	current: nid1 		-> prevScan: null		timer: start	(update nid's location in database)

				// cases 1 and 2
				if (prevScan == null)
				{
					_scanTimer.Start();
					//timers[e.ScannerId].Start();
					prevScan = Tuple.Create(barcode, barcodeType);
				}
				// cases 5,6,7
				else if (barcodeType == BarcodeType.location)
				{
					_scanTimer.Start();
					//timers[e.ScannerId].Start();
					prevScan = Tuple.Create(barcode, barcodeType);
				}
				else
				{
					if (prevScan.Item2 == BarcodeType.nid)
					{
						// case 3
						if (barcode.Equals(prevScan.Item1))
						{
							SendNotification(e.ScannerId, notifications["tryDatabase"]);
							//UpdateDatabase(e.ScannerId, barcode);
							prevScan = null;
						}
						// case 4
						else
						{
							_scanTimer.Start();
							//timers[e.ScannerId].Start();
							prevScan = Tuple.Create(barcode, barcodeType);
						}
					}
					// case 8
					else
					{
						SendNotification(e.ScannerId, notifications["tryDatabase"]);
						string location = prevScan.Item1;
						//UpdateDatabase(e.ScannerId, barcode, location);
						prevScan = null;
					}
				}
			}
		}

		private static void OnScanTimerElapsed(Object source, System.Timers.ElapsedEventArgs e)
		{
			// user didn't scan nid in time. 
			// case 9/10 : prevScan defined -> undefined
			Console.WriteLine("timer up!");


			// FIX
			_log.Error("Timed out waiting for barcode scan event");

			uint scannerCradleId = ((ScannerTimer)source).scannerId;
			SendNotification(scannerCradleId, notifications["timerUp"]);
			prevScan = null;
		}

		private static void OnLedTimerElapsed(Object source, System.Timers.ElapsedEventArgs e)
		{
			Console.WriteLine("flash toggle off");
			uint scannerCradleId = ((ScannerTimer)source).scannerId;
			LedMode ledOff = (LedMode)((ScannerTimer)source).ledOff;
			IMotorolaBarcodeScanner scanner = BarcodeScannerManager.Instance.GetScannerFromCradleId(scannerCradleId);

			scanner.Actions.ToggleLed(ledOff);
		}

		// returns "nid" if barcode scanned is recognized as NID, and "location" if recognized as location
		public static BarcodeType CheckBarcode(string barcode)
		{
			string locationFormat = @"^P[NESW]\d{4}";
			string nidFormat = @"(\d|[A-F]){10}$";

			if (EvalRegex(locationFormat, barcode))
			{
				return BarcodeType.location;
			}
			else if (EvalRegex(nidFormat, barcode))
			{
				return BarcodeType.nid;
			}
			else
			{
				return BarcodeType.None;
			}
		}

		public static Boolean EvalRegex(string rxStr, string matchStr)
		{
			Regex rx = new Regex(rxStr);
			Match match = rx.Match(matchStr);

			return match.Success;
		}

		public static void UpdateDatabase(uint scannerId, string nid, string location = null)
		{
			// Execute a (SHELL) Command that runs python script to update database
			using (var sshclient = new SshClient(ConnInfo))
			{
				sshclient.Connect();
				// C# will convert null string to empty in concatenation
				//using (var cmd = sshclient.CreateCommand("python3 /var/www/scripts/autoscan.py" + location + " " + nid))
				using (var cmd = sshclient.CreateCommand("python3 /home/jmorrison/Zebra-Scanner-Service/autoscan/dbtester.py " + nid + " " + location))
				{
					cmd.Execute();
					Console.WriteLine("Command>" + cmd.CommandText);
					Console.WriteLine("Return Value = {0}", cmd.ExitStatus);

					// user or comment exists on device, so can't take it
					//if (cmd.ExitStatus == 3)
					//{
					//	Console.WriteLine("failed to update db");
					//	//SendNotification(scannerId, notifications["deviceReserved"]);
					//	// log
					//}

					if (cmd.ExitStatus > 0)
					{
						// send notification from here so it's faster
						SendNotification(scannerId, notifications["databaseFailure"]);
						// could not connect to database, or could not commit to database, or something unexpected has occurred
						if (cmd.ExitStatus == 1)
						{
							Console.WriteLine("failed to update db");
							_log.Fatal("Error connecting to database.");
						}
						else if (cmd.ExitStatus == 2 || cmd.ExitStatus > 0)
						{
							if (location != null)
							{
								_log.Fatal("Error updating database with location=" + location + ", NID=" + nid);
							}
							else
							{
								_log.Fatal("Error removing NID=" + nid + " location info from database");
							}
						}
					}
					else
					{
						Console.WriteLine("successful update db");
						if (location != null)
						{
							_log.Debug("Successfully updated database with location=" + location + ", NID=" + nid);
						}
						else
						{
							_log.Debug("Successfully removed location info for NID=" + nid);
						}
					}
				}
				sshclient.Disconnect();
			}
		}

		public static void SendNotification(uint scannerCradleId, Tuple<LedMode?, LedMode?, int, BeepPattern?> notificationParams)
		{
			IMotorolaBarcodeScanner scanner = BarcodeScannerManager.Instance.GetScannerFromCradleId(scannerCradleId);
			//IMotorolaBarcodeScanner scanner = BarcodeScannerManager.Instance.GetDevices()[1];

			Console.WriteLine("scanner to notify: " + scanner.Info.ScannerId);

			// sound beeper
			if (notificationParams.Item4 != null)
			{
				scanner.Actions.SoundBeeper((BeepPattern)notificationParams.Item4);
			}
			// flash LED
			if (notificationParams.Item1 != null && notificationParams.Item2 != null)
			{
				scanner.Actions.ToggleLed((LedMode)notificationParams.Item1);
				//Thread.Sleep(notificationParams.Item3);
				//scanner.Actions.ToggleLed((LedMode)notificationParams.Item2);
				// start timer, and when timer is up, event handler turns off LED
				var _ledTimer = new ScannerTimer
				{
					Interval = notificationParams.Item3,
					AutoReset = false,
					scannerId = scannerCradleId,
					ledOff = (LedMode)notificationParams.Item2
				};

				//var _ledTimer = new System.Timers.Timer(notificationParams.Item3) { AutoReset = false };
				_ledTimer.Elapsed += OnLedTimerElapsed;
				Console.WriteLine("led timer started");
				_ledTimer.Start();
			}
		}

	}
}
