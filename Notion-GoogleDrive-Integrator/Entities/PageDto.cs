using Notion.Client;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Notion_GoogleDrive_Integrator.Entities
{
    public class PageDto
    {
        public TSBlock TsBlock { get; set; }

        public IBlock NotionBlock { get; set; }

        public BlockAction Action { get; set;}
    }
}
