using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using VidLex.Core.Abstractions;
using VidLex.Core.Infrastructure;

namespace VidLex.Core.Services
{
    /// <summary>
    /// Рекурсивный обход папки любой глубины. Недоступные подпапки пропускаются с предупреждением,
    /// символические ссылки и точки соединения не обходятся (защита от зацикливания),
    /// системные папки корзины и тома пропускаются.
    /// </summary>
    public sealed class VideoDiscoveryService : IVideoDiscoveryService
    {
        private static readonly string[] SkippedFolderNames = { "$RECYCLE.BIN", "System Volume Information", "RECYCLER" };
        private readonly ILogService _log;

        public VideoDiscoveryService(ILogService log)
        {
            _log = log;
        }

        public Task<DiscoveryResult> ScanAsync(string rootFolder, IEnumerable<string> extensions, IProgress<int> foundSoFar, CancellationToken ct)
        {
            var exts = new HashSet<string>(extensions.Select(e => e.ToLowerInvariant()), StringComparer.OrdinalIgnoreCase);
            return Task.Run(() => Scan(rootFolder, exts, foundSoFar, ct), ct);
        }

        private DiscoveryResult Scan(string root, HashSet<string> exts, IProgress<int> progress, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                throw new DirectoryNotFoundException($"Папка не найдена: «{root}»");

            var result = new DiscoveryResult();
            var pending = new Stack<string>();
            pending.Push(Path.GetFullPath(root));

            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var dir = pending.Pop();
                result.FoldersScanned++;

                string[] files;
                string[] subdirs;
                try
                {
                    files = Directory.GetFiles(dir);
                    subdirs = Directory.GetDirectories(dir);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException || ex is System.Security.SecurityException)
                {
                    result.InaccessibleFolders++;
                    _log?.Warning($"Нет доступа к папке, она пропущена: {dir}", stage: "Сканирование", details: ex.Message);
                    continue;
                }

                foreach (var file in files)
                {
                    var name = Path.GetFileName(file);
                    if (name.StartsWith("~$", StringComparison.Ordinal) || name.StartsWith("._", StringComparison.Ordinal))
                        continue;
                    if (exts.Contains(Path.GetExtension(file)))
                    {
                        result.Files.Add(file);
                        if (result.Files.Count % 25 == 0) progress?.Report(result.Files.Count);
                    }
                    else
                    {
                        result.SkippedUnsupported++;
                    }
                }

                // Обратный порядок, чтобы стек выдавал подпапки в алфавитном порядке.
                foreach (var sub in subdirs.OrderByDescending(d => d, NaturalStringComparer.Instance))
                {
                    if (ShouldSkipFolder(sub)) continue;
                    pending.Push(sub);
                }
            }

            result.Files.Sort((a, b) => NaturalStringComparer.Instance.Compare(
                PathUtil.GetRelativePath(root, a), PathUtil.GetRelativePath(root, b)));
            progress?.Report(result.Files.Count);
            return result;
        }

        private bool ShouldSkipFolder(string dir)
        {
            var name = Path.GetFileName(dir);
            if (SkippedFolderNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
                return true;
            try
            {
                var attrs = File.GetAttributes(dir);
                if ((attrs & FileAttributes.ReparsePoint) != 0)
                {
                    _log?.Debug($"Символическая ссылка или точка соединения не обходится: {dir}", stage: "Сканирование");
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return true;
            }
            return false;
        }
    }
}
