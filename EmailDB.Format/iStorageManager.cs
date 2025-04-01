using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EmailDB.Format;
public interface IStorageManager : IDisposable
{
    void AddEmailToFolder(string folderName, byte[] emailContent);
    void MoveEmail(EmailHashedID emailId, string sourceFolder, string targetFolder);
    void DeleteEmail(EmailHashedID emailId, string folderName);
    void UpdateEmailContent(EmailHashedID emailId, byte[] newContent);
    void CreateFolder(string folderName, string parentFolderId = null);
    void DeleteFolder(string folderName, bool deleteEmails = false);
    void Compact(string outputPath);
    void InvalidateCache();
}