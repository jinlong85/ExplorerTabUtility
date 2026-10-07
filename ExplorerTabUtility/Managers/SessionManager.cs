using System;
using System.IO;
using System.Text.Json;
using ExplorerTabUtility.Models;
using ExplorerTabUtility.Helpers;

namespace ExplorerTabUtility.Managers;

/// <summary>Reads / writes the last session (open Explorer windows and tabs) from / to %AppData%\ExplorerTabUtility\session.json.</summary>
public static class SessionManager
{
    private static readonly object FileLock = new();

    private static readonly string SessionFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Constants.AppName,
        Constants.SessionFileName);

    public static SessionData? Load()
    {
        lock (FileLock)
        {
            try
            {
                if (!File.Exists(SessionFilePath)) return null;
                var session = JsonSerializer.Deserialize<SessionData>(File.ReadAllText(SessionFilePath));
                if (session == null) return null;

                // Be tolerant with hand-edited / damaged files.
                session.Windows ??= [];
                session.Windows.RemoveAll(w => w?.Tabs == null);
                foreach (var window in session.Windows)
                    window.Tabs.RemoveAll(t => t == null || string.IsNullOrWhiteSpace(t.Location));
                session.Windows.RemoveAll(w => w.Tabs.Count == 0);
                return session;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>Writes the session atomically (temporary file + replace), so a crash / power loss never leaves a half-written file.</summary>
    public static bool Save(SessionData session)
    {
        lock (FileLock)
        {
            var tempPath = SessionFilePath + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SessionFilePath)!);
                File.WriteAllText(tempPath, JsonSerializer.Serialize(session));

                if (!File.Exists(SessionFilePath))
                    File.Move(tempPath, SessionFilePath);
                else
                {
                    try
                    {
                        File.Replace(tempPath, SessionFilePath, null);
                    }
                    catch (IOException)
                    {
                        // Some file systems don't support Replace; overwrite the complete temporary file instead.
                        File.Copy(tempPath, SessionFilePath, overwrite: true);
                        File.Delete(tempPath);
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save the session: {ex.Message}");
                try { File.Delete(tempPath); } catch { /* ignored */ }
                return false;
            }
        }
    }
}
