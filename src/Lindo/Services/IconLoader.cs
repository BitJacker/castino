using System;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Lindo.Core;

namespace Lindo.Services
{
    internal static class IconLoader
    {
        /// <summary>Carica l'icona di un'app (exe/dll/ico per i programmi, png per lo Store). Null se non disponibile.</summary>
        public static ImageSource Load(AppInfo app)
        {
            try
            {
                var source = app.DisplayIcon;
                if (string.IsNullOrWhiteSpace(source)) return null;
                source = Environment.ExpandEnvironmentVariables(source.Trim());

                if (app.Source == AppSource.Store || source.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                {
                    if (!File.Exists(source)) return null;
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 64;
                    bmp.UriSource = new Uri(source);
                    bmp.EndInit();
                    bmp.Freeze();
                    return bmp;
                }

                var file = WinPath.ExtractExecutable(source);
                var index = 0;
                var comma = source.LastIndexOf(',');
                if (comma > 0) int.TryParse(source.Substring(comma + 1).Trim(), out index);
                if (!File.Exists(file)) return null;

                var large = new IntPtr[1];
                // Prima prova a 64 px (nitida anche con lo schermo ridimensionato), poi l'API classica
                var ids = new uint[1];
                int count;
                try { count = (int)Native.PrivateExtractIcons(file, index, 64, 64, large, ids, 1, 0); }
                catch { count = 0; }
                if (count <= 0 || count == -1 || large[0] == IntPtr.Zero)
                    count = Native.ExtractIconEx(file, index, large, null, 1);
                if (count <= 0 || large[0] == IntPtr.Zero)
                {
                    if (index != 0) count = Native.ExtractIconEx(file, 0, large, null, 1);
                    if (count <= 0 || large[0] == IntPtr.Zero) return null;
                }
                try
                {
                    var img = Imaging.CreateBitmapSourceFromHIcon(large[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    img.Freeze();
                    return img;
                }
                finally
                {
                    Native.DestroyIcon(large[0]);
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
