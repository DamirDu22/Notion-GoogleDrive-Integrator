using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Notion_GoogleDrive_Integrator.Services
{
    public class FileService: IFileService
    {
        public async Task WriteToFileAsync(string text, string fileName, string filePath = "")
        {
            if (string.IsNullOrEmpty(filePath))
            {
                var docPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                filePath = Path.Combine(docPath, "Notion");
            }

            await File.WriteAllTextAsync(Path.Combine(filePath, $"{fileName}.txt"), text);
        }

        public string CreateFolder(string name)
        {
            var docPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string fullPath = Path.Combine(docPath, "Notion", name);
            Directory.CreateDirectory(fullPath);

            return fullPath;
        }
    }

    public interface IFileService
    {
        Task WriteToFileAsync(string text, string fileName, string filePath = "");
        string CreateFolder(string name);
    }
}
