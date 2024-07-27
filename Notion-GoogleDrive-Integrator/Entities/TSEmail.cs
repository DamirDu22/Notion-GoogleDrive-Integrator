using Azure;
using Azure.Data.Tables;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Notion_GoogleDrive_Integrator.Entities
{
    /// <summary>
    /// Object representing Notion block stored in table storage.
    /// </summary>
    public class TSEmail: ITableEntity
    {
        public string RowKey { get; set; } = default!;

        public string PartitionKey { get; set; } = default!;

        public string Email { get; set; } = default!;
        public DateTime EditDate { get; set; }

        public ETag ETag { get; set; } = default!;

        public DateTimeOffset? Timestamp { get; set; } = default!;
    }
}
