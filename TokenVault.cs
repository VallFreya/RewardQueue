using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace RewardQueue
{
    /// <summary>
    /// Токены Twitch на диске: JSON, зашифрованный DPAPI (ключ привязан к учётной записи Windows, файл нельзя расшифровать
    /// под другим пользователем или на другом компьютере). Токены в журнал и в config.json не попадают.
    /// </summary>
    internal static class TokenVault
    {
        public const string FileName = "twitch_auth.bin";

        // Дополнительная «соль» DPAPI: другая программа того же пользователя не расшифрует файл вызовом по умолчанию
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RewardQueue.TwitchAuth.v1");

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int cbData;
            public IntPtr pbData;
        }

        private const uint CryptProtectUiForbidden = 0x1;

        [DllImport("crypt32.dll", SetLastError = true)]
        private static extern int CryptProtectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob dataOut);

        [DllImport("crypt32.dll", SetLastError = true)]
        private static extern int CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob dataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);

        public static void Save(string path, StoredAuth auth)
        {
            string json = JsonSerializer.Serialize(auth, TwitchJsonContext.Default.StoredAuth);
            AtomicFileBytes(path, Transform(Encoding.UTF8.GetBytes(json), protect: true));
        }

        /// <summary>Сохранённый вход или null: файла нет, он повреждён или зашифрован для другого пользователя</summary>
        public static StoredAuth? Load(string path)
        {
            if (!File.Exists(path)) return null;

            try
            {
                byte[] plain = Transform(File.ReadAllBytes(path), protect: false);
                var auth = JsonSerializer.Deserialize(Encoding.UTF8.GetString(plain), TwitchJsonContext.Default.StoredAuth);
                return auth != null && !string.IsNullOrEmpty(auth.refresh_token) ? auth : null;
            }
            catch (Exception ex)
            {
                // Войти заново проще, чем разбираться: нечитаемый файл просто игнорируется (токены в нём всё равно не восстановить)
                Plugin.LogWarn($"Saved Twitch login could not be read and is ignored: {ex.Message}");
                return null;
            }
        }

        public static void Delete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                Plugin.LogWarn($"Could not delete the saved Twitch login: {ex.Message}");
            }
        }

        // Запись через временный файл, как у остальных файлов плагина
        private static void AtomicFileBytes(string path, byte[] bytes)
        {
            string temp = path + ".tmp";
            try
            {
                File.WriteAllBytes(temp, bytes);
                File.Move(temp, path, overwrite: true);
            }
            catch
            {
                try { File.Delete(temp); } catch { }
                throw;
            }
        }

        private static byte[] Transform(byte[] input, bool protect)
        {
            IntPtr inPtr = Marshal.AllocHGlobal(Math.Max(1, input.Length));
            IntPtr entropyPtr = Marshal.AllocHGlobal(Entropy.Length);
            var output = new DataBlob();
            try
            {
                Marshal.Copy(input, 0, inPtr, input.Length);
                Marshal.Copy(Entropy, 0, entropyPtr, Entropy.Length);
                var dataIn = new DataBlob { cbData = input.Length, pbData = inPtr };
                var entropy = new DataBlob { cbData = Entropy.Length, pbData = entropyPtr };

                int ok = protect
                    ? CryptProtectData(ref dataIn, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output)
                    : CryptUnprotectData(ref dataIn, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output);
                if (ok == 0) throw new CryptographicExceptionWithCode(Marshal.GetLastWin32Error());

                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return result;
            }
            finally
            {
                if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
                Marshal.FreeHGlobal(inPtr);
                Marshal.FreeHGlobal(entropyPtr);
            }
        }

        private sealed class CryptographicExceptionWithCode : Exception
        {
            public CryptographicExceptionWithCode(int code) : base($"DPAPI call failed (Win32 error {code})") { }
        }
    }
}
