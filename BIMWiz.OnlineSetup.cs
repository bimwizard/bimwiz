using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: AssemblyTitle("BIMWiz Online Setup")]
[assembly: AssemblyDescription("Download the current BIMWiz installer package from GitHub")]
[assembly: AssemblyCompany("Acube Developments")]
[assembly: AssemblyVersion("1.0.0.0")]

namespace BIMWiz.OnlineSetup
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm());
        }
    }

    public sealed class ReleaseInfo
    {
        public int schemaVersion { get; set; }
        public string version { get; set; }
        public string packageUrl { get; set; }
        public string sha256 { get; set; }
        public long sizeBytes { get; set; }
    }

    internal static class SetupFiles
    {
        internal const string ManifestUrl = "https://raw.githubusercontent.com/bimwizard/bimwiz/main/bimwiz-release.json";
        private const long MaxPackageBytes = 200L * 1024 * 1024;
        private const long MaxExtractedBytes = 500L * 1024 * 1024;

        internal static ReleaseInfo ReadRelease(string json)
        {
            var serializer = new JavaScriptSerializer { MaxJsonLength = 65536 };
            var release = serializer.Deserialize<ReleaseInfo>(json);
            if (release == null || release.schemaVersion != 1 ||
                string.IsNullOrWhiteSpace(release.version) || release.version.Length > 80 ||
                release.sha256 == null || !Regex.IsMatch(release.sha256, "\\A[0-9a-fA-F]{64}\\z") ||
                release.sizeBytes <= 0 || release.sizeBytes > MaxPackageBytes)
                throw new InvalidDataException("The release information is incomplete or unsupported.");
            ValidateUrl(release.packageUrl);
            return release;
        }

        private static Uri ValidateUrl(string value)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || uri.Scheme != "https" ||
                !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) ||
                !uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
                !uri.AbsolutePath.StartsWith("/bimwizard/bimwiz/", StringComparison.Ordinal) ||
                !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                throw new InvalidDataException("The download must come from the BIMWiz GitHub repository over HTTPS.");
            return uri;
        }

        internal static void Download(string url, Stream destination, long limit,
            CancellationToken cancellation, Action<int> progress)
        {
            var request = (HttpWebRequest)WebRequest.Create(ValidateUrl(url));
            request.AllowAutoRedirect = false;
            request.Timeout = 45000;
            request.ReadWriteTimeout = 45000;
            request.UserAgent = "BIMWiz-Online-Setup/1.0";
            request.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
            using (cancellation.Register(request.Abort))
            using (var response = (HttpWebResponse)request.GetResponse())
            {
                if (response.StatusCode != HttpStatusCode.OK)
                    throw new IOException("GitHub returned " + (int)response.StatusCode + ".");
                if (response.ContentLength > limit)
                    throw new InvalidDataException("The download exceeds its expected size.");
                using (var input = response.GetResponseStream())
                {
                    byte[] buffer = new byte[81920];
                    long total = 0;
                    int count;
                    while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        total += count;
                        if (total > limit) throw new InvalidDataException("The download exceeds its expected size.");
                        destination.Write(buffer, 0, count);
                        if (progress != null && response.ContentLength > 0)
                            progress((int)Math.Min(100, total * 100 / response.ContentLength));
                    }
                    if (response.ContentLength >= 0 && total != response.ContentLength)
                        throw new IOException("The download was interrupted. Please try again.");
                }
            }
        }

        internal static ReleaseInfo FetchRelease(CancellationToken cancellation)
        {
            using (var buffer = new MemoryStream())
            {
                Download(ManifestUrl, buffer, 65536, cancellation, null);
                return ReadRelease(System.Text.Encoding.UTF8.GetString(buffer.ToArray()).TrimStart('\uFEFF'));
            }
        }

        internal static void VerifyPackage(string path, ReleaseInfo release)
        {
            if (new FileInfo(path).Length != release.sizeBytes)
                throw new InvalidDataException("The package size does not match the published release.");
            using (var stream = File.OpenRead(path))
            using (var hash = SHA256.Create())
            {
                string actual = BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "");
                if (!actual.Equals(release.sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The package checksum does not match. No installer was started.");
            }
        }

        internal static string ExtractPackage(string zipPath, string destination, CancellationToken cancellation)
        {
            string root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                long expanded = 0;
                if (zip.Entries.Count > 2000) throw new InvalidDataException("The package contains too many files.");
                // Validate the entire archive before writing any files.
                foreach (var entry in zip.Entries)
                {
                    string name = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                    if (Path.IsPathRooted(name) || name.Contains(":") ||
                        !Path.GetFullPath(Path.Combine(root, name)).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("The package contains an unsafe file path.");
                    expanded = checked(expanded + entry.Length);
                    if (expanded > MaxExtractedBytes) throw new InvalidDataException("The extracted package is too large.");
                }
                Directory.CreateDirectory(root);
                foreach (var entry in zip.Entries)
                {
                    cancellation.ThrowIfCancellationRequested();
                    string target = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                    if (entry.Name.Length == 0) { Directory.CreateDirectory(target); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    entry.ExtractToFile(target, false);
                }
            }
            string installer = Path.Combine(root, "installer.exe");
            if (!File.Exists(installer) || !Directory.Exists(Path.Combine(root, "payload")))
                throw new InvalidDataException("The package is missing its installer or Revit files.");
            return installer;
        }

        internal static string PreparePackage(ReleaseInfo release, string cacheRoot,
            CancellationToken cancellation, Action<int> progress)
        {
            string session = Path.Combine(Path.GetFullPath(cacheRoot), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(session);
            string zipPath = Path.Combine(session, "BIMWiz-Installer-Package.zip");
            using (var file = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write))
                Download(release.packageUrl, file, release.sizeBytes, cancellation, progress);
            VerifyPackage(zipPath, release);
            return ExtractPackage(zipPath, Path.Combine(session, "package"), cancellation);
        }
    }

    internal sealed class SetupForm : Form
    {
        private readonly Label _status = new Label();
        private readonly ProgressBar _progress = new ProgressBar();
        private readonly Button _download = new Button();
        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();
        private bool _busy;

        internal SetupForm()
        {
            Text = "BIMWiz Online Setup";
            ClientSize = new Size(500, 330);
            Font = new Font("Segoe UI", 10);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            var logo = new PictureBox { Bounds = new Rectangle(48, 12, 404, 95), SizeMode = PictureBoxSizeMode.Zoom };
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("BIMWiz.logo"))
            { if (stream != null) using (var image = new Bitmap(stream)) logo.Image = CropLogo(image); }
            var title = new Label { Text = "Install BIMWiz for Revit", Bounds = new Rectangle(24, 111, 452, 28), Font = new Font(Font, FontStyle.Bold) };
            var description = new Label { Text = "Download the latest package, then choose your Revit\nversion in the installer. Internet access is required.", Bounds = new Rectangle(24, 148, 452, 52) };
            _status.Text = "Ready to download.";
            _status.SetBounds(24, 204, 452, 24);
            _progress.SetBounds(24, 234, 452, 14);
            _download.Text = "Download && open installer";
            _download.SetBounds(24, 264, 310, 40);
            _download.Click += DownloadClick;
            var close = new Button { Text = "Close", Bounds = new Rectangle(348, 264, 128, 40) };
            close.Click += delegate { Close(); };
            Controls.AddRange(new Control[] { logo, title, description, _status, _progress, _download, close });
            FormClosing += delegate { _cancellation.Cancel(); };
        }

        private static Image CropLogo(Bitmap image)
        {
            int left = image.Width, top = image.Height, right = -1, bottom = -1;
            for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
            {
                Color color = image.GetPixel(x, y);
                if (color.A > 16 && (color.R < 245 || color.G < 245 || color.B < 245))
                { left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y); }
            }
            if (right < left) return new Bitmap(image);
            return image.Clone(Rectangle.FromLTRB(Math.Max(0, left - 12), Math.Max(0, top - 12),
                Math.Min(image.Width, right + 13), Math.Min(image.Height, bottom + 13)), image.PixelFormat);
        }

        private async void DownloadClick(object sender, EventArgs args)
        {
            if (_busy) return;
            _busy = true;
            _download.Enabled = false;
            _status.Text = "Checking the latest BIMWiz release...";
            _progress.Style = ProgressBarStyle.Marquee;
            try
            {
                var release = await Task.Run(() => SetupFiles.FetchRelease(_cancellation.Token));
                _cancellation.Token.ThrowIfCancellationRequested();
                _status.Text = "Downloading BIMWiz " + release.version + "...";
                _progress.Style = ProgressBarStyle.Continuous;
                var progress = new Progress<int>(value => { if (!IsDisposed) _progress.Value = value; });
                string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BIMWiz", "OnlineSetup");
                string installer = await Task.Run(() => SetupFiles.PreparePackage(release, cache, _cancellation.Token,
                    value => ((IProgress<int>)progress).Report(value)));
                _cancellation.Token.ThrowIfCancellationRequested();
                _progress.Value = 100;
                _status.Text = "Package verified. Opening the installer...";
                var process = Process.Start(new ProcessStartInfo(installer) { WorkingDirectory = Path.GetDirectoryName(installer), UseShellExecute = false });
                if (process == null) throw new IOException("Windows could not open the installer.");
                process.Dispose();
                Close();
            }
            catch (Exception ex)
            {
                if (_cancellation.IsCancellationRequested || IsDisposed) return;
                _progress.Style = ProgressBarStyle.Continuous;
                _progress.Value = 0;
                _status.Text = "Download failed. You can try again.";
                MessageBox.Show(this, ex.Message + "\n\nYou can also download the offline ZIP from the BIMWiz website.",
                    "BIMWiz Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { if (!IsDisposed) { _busy = false; _download.Enabled = true; } }
        }
    }
}
