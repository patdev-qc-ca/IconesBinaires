
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace IconesBinaires
{
    internal class Program
    {
        static async Task<int> Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            if (args.Length < 2)
            {
                Console.WriteLine("Utilisation : IconScanner <dossierSource> <dossierDestination>");
                Console.WriteLine("Exemple   : IconScanner C:\\ C:\\IconsExport");
                return 1;
            }

            string sourceRoot = args[0];
            string outputRoot = args[1];

            if (!Directory.Exists(sourceRoot))
            {
                Console.WriteLine($"Dossier source introuvable : {sourceRoot}");
                return 1;
            }

            Directory.CreateDirectory(outputRoot);

            Console.WriteLine($"Source      : {sourceRoot}");
            Console.WriteLine($"Destination : {outputRoot}");
            Console.WriteLine();

            var scanner = new FileScanner(new[]
            {
                ".exe", ".dll", ".ico"
            });

            var iconExtractor = new IconExtractor();
            var iconHasher = new IconHasher();
            var iconSaver = new IconSaver(outputRoot);

            var uniqueHashes = new ConcurrentDictionary<string, bool>();

            var files = scanner.EnumerateFiles(sourceRoot);

            int totalFiles = 0;
            int filesWithIcons = 0;
            int iconsSaved = 0;

            var swTotal = Stopwatch.StartNew();

            // Traitement parallèle des fichiers
            await Parallel.ForEachAsync(files, new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount
            }, async (filePath, _) =>
            {
                try
                {
                    Interlocked.Increment(ref totalFiles);

                    var extractedIcons = iconExtractor.ExtractIcons(filePath);
                    if (extractedIcons.Count == 0)
                        return;

                    Interlocked.Increment(ref filesWithIcons);

                    foreach (var iconInfo in extractedIcons)
                    {
                        using (var bmp = iconInfo.Bitmap)
                        {
                            // Hash basé sur les pixels pour éviter doublons
                            string hash = iconHasher.ComputeHash(bmp);

                            if (uniqueHashes.TryAdd(hash, true))
                            {
                                string baseName = iconSaver.BuildBaseName(filePath, iconInfo.Size);
                                await iconSaver.SaveIconAsync(bmp, baseName);

                                Interlocked.Increment(ref iconsSaved);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Erreur fichier {filePath}: {ex.Message}");
                }
            });

            swTotal.Stop();

            Console.WriteLine();
            Console.WriteLine("Terminé.");
            Console.WriteLine($"Fichiers scannés       : {totalFiles}");
            Console.WriteLine($"Fichiers avec icônes   : {filesWithIcons}");
            Console.WriteLine($"Icônes uniques sauvées : {iconsSaved}");
            Console.WriteLine($"Durée totale           : {swTotal.Elapsed}");

            return 0;
        }
    }

    /// <summary>
    /// Parcours récursif des fichiers avec filtre sur les extensions.
    /// </summary>
    internal class FileScanner
    {
        private readonly HashSet<string> _extensions;

        public FileScanner(IEnumerable<string> extensions)
        {
            _extensions = new HashSet<string>(
                extensions.Select(e => e.ToLowerInvariant()),
                StringComparer.OrdinalIgnoreCase);
        }

        public IEnumerable<string> EnumerateFiles(string root)
        {
            var dirs = new Stack<string>();
            dirs.Push(root);

            while (dirs.Count > 0)
            {
                string currentDir = dirs.Pop();

                string[] subDirs;
                try
                {
                    subDirs = Directory.GetDirectories(currentDir);
                }
                catch
                {
                    continue;
                }

                foreach (var d in subDirs)
                    dirs.Push(d);

                string[] files;
                try
                {
                    files = Directory.GetFiles(currentDir);
                }
                catch
                {
                    continue;
                }

                foreach (var file in files)
                {
                    string ext = Path.GetExtension(file).ToLowerInvariant();
                    if (_extensions.Contains(ext))
                        yield return file;
                }
            }
        }
    }

    /// <summary>
    /// Représente une icône extraite d'un fichier.
    /// </summary>
    internal sealed class ExtractedIconInfo
    {
        public Bitmap Bitmap { get; }
        public Size Size { get; }

        public ExtractedIconInfo(Bitmap bitmap, Size size)
        {
            Bitmap = bitmap;
            Size = size;
        }
    }

    /// <summary>
    /// Extraction d'icônes depuis EXE, DLL, ICO.
    /// Version simple : une icône principale haute qualité par fichier.
    /// </summary>
    internal class IconExtractor
    {
        // Taille cible "haute qualité" pour PNG
        private static readonly Size[] PreferredSizes =
        {
            new Size(256, 256),
            new Size(128, 128),
            new Size(64, 64),
            new Size(48, 48),
            new Size(32, 32),
            new Size(16, 16)
        };

        public List<ExtractedIconInfo> ExtractIcons(string filePath)
        {
            string ext = Path.GetExtension(filePath).ToLowerInvariant();

            if (ext == ".ico")
                return ExtractFromIco(filePath);

            if (ext == ".exe" || ext == ".dll")
                return ExtractFromExecutable(filePath);

            return new List<ExtractedIconInfo>();
        }

        private List<ExtractedIconInfo> ExtractFromIco(string filePath)
        {
            var list = new List<ExtractedIconInfo>();

            try
            {
                using Icon icon = new Icon(filePath);
                using Bitmap bmp = icon.ToBitmap();

                var bestSize = ChooseBestSize(bmp.Size);
                using Bitmap resized = ResizeIfNeeded(bmp, bestSize);

                list.Add(new ExtractedIconInfo((Bitmap)resized.Clone(), resized.Size));
            }
            catch
            {
                // Fichier ICO corrompu ou invalide
            }

            return list;
        }

        private List<ExtractedIconInfo> ExtractFromExecutable(string filePath)
        {
            var list = new List<ExtractedIconInfo>();

            try
            {
                using Icon? associatedIcon = Icon.ExtractAssociatedIcon(filePath);

                if (associatedIcon == null)
                    return list;

                using Bitmap bmp = associatedIcon.ToBitmap();
                var bestSize = ChooseBestSize(bmp.Size);
                using Bitmap resized = ResizeIfNeeded(bmp, bestSize);

                list.Add(new ExtractedIconInfo((Bitmap)resized.Clone(), resized.Size));
            }
            catch
            {
                // Pas d'icône, ou problème de lecture
            }

            return list;
        }

        private static Size ChooseBestSize(Size original)
        {
            // Si l’image est déjà grande, on garde sa taille
            foreach (var size in PreferredSizes)
            {
                if (original.Width >= size.Width && original.Height >= size.Height)
                    return size;
            }

            // Sinon on garde la taille originale (petite)
            return original;
        }

        private static Bitmap ResizeIfNeeded(Bitmap source, Size targetSize)
        {
            if (source.Width == targetSize.Width && source.Height == targetSize.Height)
                return (Bitmap)source.Clone();

            var dest = new Bitmap(targetSize.Width, targetSize.Height, source.PixelFormat);
            dest.SetResolution(source.HorizontalResolution, source.VerticalResolution);

            using (var g = Graphics.FromImage(dest))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;

                g.DrawImage(source, new Rectangle(Point.Empty, targetSize));
            }

            return dest;
        }
    }

    /// <summary>
    /// Calcul d'un hash SHA-256 à partir des pixels de l'image.
    /// </summary>
    internal class IconHasher
    {
        public string ComputeHash(Bitmap bmp)
        {
            // On convertit en format standard (32bpp ARGB) pour éviter
            // les différences de format qui ne changent pas visuellement l'image.
            using var normalized = new Bitmap(bmp.Width, bmp.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            normalized.SetResolution(bmp.HorizontalResolution, bmp.VerticalResolution);

            using (var g = Graphics.FromImage(normalized))
            {
                g.DrawImageUnscaled(bmp, 0, 0);
            }

            var data = normalized.LockBits(
                new Rectangle(0, 0, normalized.Width, normalized.Height),
                System.Drawing.Imaging.ImageLockMode.ReadOnly,
                normalized.PixelFormat);

            try
            {
                int bytes = Math.Abs(data.Stride) * data.Height;
                byte[] buffer = new byte[bytes];
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buffer, 0, bytes);

                using var sha = SHA256.Create();
                byte[] hashBytes = sha.ComputeHash(buffer);
                return BitConverter.ToString(hashBytes).Replace("-", "");
            }
            finally
            {
                normalized.UnlockBits(data);
            }
        }
    }

    /// <summary>
    /// Sauvegarde des icônes en PNG dans une arborescence de sortie.
    /// </summary>
    internal class IconSaver
    {
        private readonly string _outputRoot;

        public IconSaver(string outputRoot)
        {
            _outputRoot = outputRoot;
        }

        public string BuildBaseName(string filePath, Size size)
        {
            string fileName = Path.GetFileNameWithoutExtension(filePath);
            string safeName = MakeSafeFileName(fileName);

            return $"{safeName}_{size.Width}x{size.Height}";
        }

        public async Task SaveIconAsync(Bitmap bitmap, string baseName)
        {
            string subFolder = Path.Combine(_outputRoot, $"{bitmap.Width}x{bitmap.Height}");
            Directory.CreateDirectory(subFolder);

            string filePath = Path.Combine(subFolder, baseName + ".png");

            // Gestion du cas où le nom existe déjà : suffixe numérique
            int counter = 1;
            string finalPath = filePath;
            while (File.Exists(finalPath))
            {
                finalPath = Path.Combine(subFolder, $"{baseName}_{counter}.png");
                counter++;
            }

            using var ms = new MemoryStream();
            bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;

            using var fs = new FileStream(finalPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await ms.CopyToAsync(fs);
        }

        private static string MakeSafeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');

            return name;
        }
    }
}

