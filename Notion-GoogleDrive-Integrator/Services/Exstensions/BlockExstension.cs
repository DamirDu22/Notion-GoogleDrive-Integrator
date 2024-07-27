using Google.Protobuf.WellKnownTypes;
using Notion.Client;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace Notion_GoogleDrive_Integrator.Services.Exstensions
{
    public static class BlockExstension
    {
        //public static string GetText(this IBlock block)
        //{
        //    switch (block.Type)
        //    {
        //        case BlockType.Paragraph:
        //            return ((ParagraphBlock)block)?.Paragraph?.RichText?.FirstOrDefault()?.PlainText ?? "";
        //        case BlockType.BulletedListItem:
        //            return $"-{((BulletedListItemBlock)block)?.BulletedListItem?.RichText?.FirstOrDefault()?.PlainText}";
        //        case BlockType.NumberedListItem:
        //            return $"-{((NumberedListItemBlock)block)?.NumberedListItem?.RichText?.FirstOrDefault()?.PlainText}";
        //        default:
        //            return "";
        //    }
        //}
    }
}
