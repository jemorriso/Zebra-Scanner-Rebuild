using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Topshelf;

namespace ZebraScannerRebuild
{
	class Program
	{
		static void Main(string[] args)
		{
			var rc = HostFactory.Run(x =>                                   //1
			{
				x.Service<ZebraScannerRebuild>(s =>                               //2
				{
					s.ConstructUsing(tc => new ZebraScannerRebuild());   //3
					s.WhenStarted(tc => tc.Start());                         //4
					s.WhenStopped(tc => tc.Stop());                          //5
				});
				x.RunAsLocalSystem();                                       //6

				x.SetDescription("Manages handling of the bluetooth Zebra barcode scanner");
				x.SetDisplayName("Zebra Barcode Scanner Service");
				x.SetServiceName("Zebra Barcode Scanner Service");                                  //9
			});                                                             //10

			var exitCode = (int)Convert.ChangeType(rc, rc.GetTypeCode());  //11
			Environment.ExitCode = exitCode;
		}
	}
}
