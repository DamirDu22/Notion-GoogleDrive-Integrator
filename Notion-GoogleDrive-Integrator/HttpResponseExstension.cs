using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Notion_GoogleDrive_Integrator
{
    public static class HttpResponseExstension
    {
        public static void WriteToConsole(this HttpResponseMessage message)
        {
            Console.WriteLine(message.Content.ToString());
        }
    }
}
