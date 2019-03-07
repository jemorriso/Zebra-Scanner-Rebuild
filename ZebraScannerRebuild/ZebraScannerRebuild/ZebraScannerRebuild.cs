using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using System.Threading;

using Motorola.Snapi;
using Motorola.Snapi.Constants.Enums;
using Motorola.Snapi.Constants;
using Motorola.Snapi.EventArguments;

using Renci.SshNet;

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


	class ZebraScannerRebuild
	{
		private static ConnectionInfo ConnInfo;

		private static Dictionary<string, Tuple<LedMode?, LedMode?, int, BeepPattern?>> notifications = new Dictionary<string, Tuple<LedMode?, LedMode?, int, BeepPattern?>>();

		//private static System.Timers.Timer _scanTimer = new System.Timers.Timer(5000) { AutoReset = false };
		//private static System.Timers.Timer _ledTimer = new System.Timers.Timer(100) { AutoReset = false };

		// FIX THIS
		private static List<IMotorolaBarcodeScanner> scannerList;


		public void Start()
		{
			BarcodeScannerManager.Instance.Open();

			BarcodeScannerManager.Instance.RegisterForEvents(EventType.Barcode, EventType.Pnp);

			BarcodeScannerManager.Instance.DataReceived += OnDataReceived;
			BarcodeScannerManager.Instance.ScannerAttached += OnScannerAttached;
			BarcodeScannerManager.Instance.ScannerDetached += OnScannerDetached;

			// Setup SSH connection info for remote inventory database access
			ConnInfo = new ConnectionInfo("jmorrison", "jmorrison",
				new AuthenticationMethod[] {
					// Password based Authentication
					new PasswordAuthenticationMethod("jmorrison","Pa$$wordjm")
				}
			);

			//_scanTimer.Elapsed += OnScanTimerElapsed;
			//_ledTimer.Elapsed += OnLedTimerElapsed;

			notifications.Add("tryDatabase", Tuple.Create((LedMode?)LedMode.GreenOn, (LedMode?)LedMode.GreenOff, 1000, (BeepPattern?)null));
			notifications.Add("timerUp", Tuple.Create((LedMode?)LedMode.RedOn, (LedMode?)LedMode.RedOff, 50, (BeepPattern?)BeepPattern.TwoLowShort));

			scannerList = BarcodeScannerManager.Instance.GetDevices();
			Console.WriteLine("number of connected scanners: " + scannerList.Count);


			var connectedScanner = scannerList[0];
			Console.WriteLine("Scanner hostmode: " + connectedScanner.Info.UsbHostMode);
			Console.WriteLine("Scanner manufactured: " + connectedScanner.Info.DateOfManufacture);
			Console.WriteLine("Scanner PID: " + connectedScanner.Info.ProductId);
			Console.WriteLine("Scanner model #: " + connectedScanner.Info.ModelNumber);
			Console.WriteLine("Scanner ID #: " + connectedScanner.Info.ScannerId);

			connectedScanner = scannerList[1];
			Console.WriteLine("Scanner hostmode: " + connectedScanner.Info.UsbHostMode);
			Console.WriteLine("Scanner manufactured: " + connectedScanner.Info.DateOfManufacture);
			Console.WriteLine("Scanner PID: " + connectedScanner.Info.ProductId);
			Console.WriteLine("Scanner model #: " + connectedScanner.Info.ModelNumber);
			Console.WriteLine("Scanner ID #: " + connectedScanner.Info.ScannerId);

		}

		public void Stop()
		{
			BarcodeScannerManager.Instance.Close();
		}

		private static void OnScannerAttached(object sender, PnpEventArgs e)
		{
			// log scanner attached
			Console.WriteLine("Scanner id=" + e.ScannerId + " attached");
		}

		private static void OnScannerDetached(object sender, PnpEventArgs e)
		{
			// improve logging
			Console.WriteLine("Scanner id=" + e.ScannerId + " detached");
		}

		private static void OnDataReceived(object sender, BarcodeScanEventArgs e)
		{
			Console.WriteLine("Barcode scan detected from scanner " + e.ScannerId + ": " + e.Data);

			string barcode = e.Data.ToUpper().Trim();

			//UpdateDatabase(e.ScannerId, barcode);
			var _scanTimer = new ScannerTimer
			{
				Interval = 5000,
				AutoReset = false,
				scannerId = e.ScannerId,
				ledOff = null
			};
			_scanTimer.Elapsed += OnScanTimerElapsed;
			_scanTimer.Start();
			SendNotification(e.ScannerId, notifications["tryDatabase"]);
		}

		private static void OnScanTimerElapsed(Object source, System.Timers.ElapsedEventArgs e)
		{
			// user didn't scan nid in time. Either prevNid or location is set; nullify both
			// case 9/10 : either defined -> both undefined.
			//prevScan = null;
			Console.WriteLine("timer up!");

			uint scannerCradleId = ((ScannerTimer)source).scannerId;
			//IMotorolaBarcodeScanner scanner = BarcodeScannerManager.Instance.GetScannerFromId(scannerId);
			SendNotification(scannerCradleId, notifications["timerUp"]);
		}

		private static void OnLedTimerElapsed(Object source, System.Timers.ElapsedEventArgs e)
		{
			Console.WriteLine("flash toggle off");
			uint scannerCradleId = ((ScannerTimer)source).scannerId;
			LedMode ledOff = (LedMode)((ScannerTimer)source).ledOff;
			IMotorolaBarcodeScanner scanner = BarcodeScannerManager.Instance.GetScannerFromCradleId(scannerCradleId);

			scanner.Actions.ToggleLed(ledOff);
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
					if (cmd.ExitStatus == 3)
					{
						Console.WriteLine("failed to update db");
						//SendNotification(scannerId, notifications["deviceReserved"]);
						// log
					}
					// could not connect to database, or could not commit to database, or something unexpected has occurred
					else if (cmd.ExitStatus == 1 || cmd.ExitStatus == 2 || cmd.ExitStatus > 0)
					{
						Console.WriteLine("failed to update db");

						// send notification from here so it's faster
						//SendNotification(scannerId, notifications["databaseFailure"]);
						//if (location != null)
						//{
						//	_log.Fatal("Error connecting to database or updating database with location=" + location + ", NID=" + nid);
						//}
						//else
						//{
						//	_log.Fatal("Error connecting to database or removing NID=" + nid + " from database");
						//}
					}
					else
					{
						Console.WriteLine("successful update db");

						//// **** fix this to ensure actually successful database update from autoscan.py
						//if (location != null)
						//{
						//	_log.Debug("Successfully updated database with location=" + location + ", NID=" + nid);
						//}
						//else
						//{
						//	_log.Debug("Successfully removed NID=" + nid + " from its location. Device is still in database, without a location");
						//}
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
