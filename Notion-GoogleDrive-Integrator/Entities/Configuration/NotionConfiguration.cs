using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Notion_GoogleDrive_Integrator.Entities.Configuration
{
    public class NotionConfiguration
    {
        public string NotionBaseUrl { get; set; }
        public string NotionSecret { get; set; }
        public string NotionVersionHeaderName { get; set; }
        public string NotionVersionHeaderValue { get; set; }
        public string ResourcesPageId { get; set; }

    }
}
