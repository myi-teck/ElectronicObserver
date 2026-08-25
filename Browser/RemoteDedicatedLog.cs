//============================================================================================
// [task14] 専用ログファイル書き込み（追記・日付連番ローテーション・保持期間クリーンアップ）
// 本体ブラウザログ（eolog.log）と混ざらない専用ファイルへ出力する。
// 設計: plans/task14_remote-doubleclick-fix-and-event-logging.md §3.6
//============================================================================================
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Browser
{
    /// <summary>
    /// [task14] 専用ログファイル書き込み（追記・日付連番ローテーション・保持期間クリーンアップ）。
    /// 本体ブラウザログ（eolog.log）と混ざらない専用ファイルへ出力する。
    /// </summary>
    internal sealed class RemoteDedicatedLog
    {
        /// <summary> クリック・タッチイベント専用ログ（logs/remote_event_log_YYYYMMDD.log） </summary>
        public static readonly RemoteDedicatedLog Event = new RemoteDedicatedLog("remote_event_log");
        /// <summary> RemoteController サーバ動作専用ログ（logs/remotecontroller_YYYYMMDD.log） </summary>
        public static readonly RemoteDedicatedLog Controller = new RemoteDedicatedLog("remotecontroller");

        private const int MAX_SEQUENCE = 99; // [Minor-S3] 連番上限（無限ループ防止）
        // [task14 Q9] サーバ側ログ記録 ON/OFF スイッチ（メモリ内・非永続・既定 ON・即時反映）。
        // Settings/ XML は変更しない（制約）。volatile で複数スレッドから安全に読取。
        // [v1.3] volatile は単一読み書きの可視性・アトミック性を保証するが、SetEnabled の
        // 複合操作（比較→設定→ログ記録→_lastDisabledAt 更新）の原子性は保証しない（TOCTOU）。
        // そこで SetEnabled の複合操作全体を _logToggleLock で排他制御する（一次 Critical-1・二次 Critical-2 反映）。
        private static volatile bool _logEnabled = true;
        private static DateTime _lastDisabledAt = DateTime.MinValue; // [Q9] OFF 開始時刻（§3.8.4）
        // [v1.3] SetEnabled の複合操作を排他制御するロック（TOCTOU 解消）。
        // _lastDisabledAt は volatile ではなく、常に _logToggleLock 内で読み書きする（スレッドセーフ）。
        private static readonly object _logToggleLock = new object();

        /// <summary> [task14 Q9] イベントログ記録の現在状態（既定 ON）。 </summary>
        public static bool IsEnabled => _logEnabled;

        /// <summary>
        /// [task14 Q9] ログ記録 ON/OFF を即時切り替え（メモリ内フラグ・設定変更は次回 Append から反映）。
        /// 切り替え操作自体を 1 行記録（§3.8.3）。OFF 開始時刻を保持（§3.8.4）。
        /// [v1.3] 複合操作は _logToggleLock で排他制御（TOCTOU 解消・並行 SetEnabled で切り替えログの欠落を防止）。
        /// </summary>
        public static void SetEnabled(bool enabled, string reason)
        {
            lock (_logToggleLock)
            {
                var wasEnabled = _logEnabled;
                if (wasEnabled == enabled) return; // 変化なし（lock 内で判定・並行呼び出しで重複ログを防止）
                _logEnabled = enabled;
                // [Q9] 切り替え操作自体を記録（動作ログは §3.8.5 のとおり OFF 対象外のため必ず記録）。
                // Controller.Append は _writeLock で内部シリアライズされる（§3.6・Append の設計参照）。
                // 注: Controller は Event とは異なり OFF 対象外（§3.8.5）のため、_logEnabled の
                // 状態に関わらず必ず追記される。Event.Append のみ _logEnabled でゲートされる。
                string newState = enabled ? "ON" : "OFF";
                Controller.Append($"[LogToggle] ログ記録 {newState} に変更: wasEnabled={wasEnabled}, reason={reason ?? "N/A"}");
                if (!enabled)
                {
                    _lastDisabledAt = DateTime.Now; // [Q9] OFF 開始時刻記録（§3.8.4・lock 内で更新）
                }
                else
                {
                    // [Q9] ON 復帰時: OFF 期間の記録欠落を明示する（§3.8.4）
                    if (_lastDisabledAt > DateTime.MinValue)
                    {
                        Controller.Append($"[LogToggle] ログ記録 ON に復帰（{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}）。OFF 期間（{_lastDisabledAt:yyyy-MM-dd HH:mm:ss.fff} 〜 {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}）のログは記録されていない（欠落）。");
                        _lastDisabledAt = DateTime.MinValue;
                    }
                }
            }
        }

        private readonly string _prefix;
        private readonly object _writeLock = new object();
        private static string _dir;
        private static int _retentionDays = 7;           // 保持期間（日）・Q4
        private static long _maxFileSize = 10L * 1024 * 1024; // 単一ファイル最大サイズ（10MB）・Q4
        private static DateTime _lastCleanupDate = DateTime.MinValue;
        private static bool _initialized;
        private static bool _seqOverflowWarned;
        private static readonly object _initLock = new object();

        private RemoteDedicatedLog(string prefix) { _prefix = prefix; }

        /// <summary>
        /// 初期化（RemoteController コンストラクタ先頭から一度だけ呼ぶ・冪等）。
        /// [Minor-S4] Init が未呼び出しの場合、Append から EnsureInitialized() がセーフネットで呼ぶ。
        /// </summary>
        public static void Init(int retentionDays = 7, long maxFileSize = 10L * 1024 * 1024)
        {
            lock (_initLock)
            {
                _retentionDays = retentionDays;
                _maxFileSize = maxFileSize;
                if (_initialized) return;
                _dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                try { Directory.CreateDirectory(_dir); }
                catch (Exception ex)
                {
                    // AGENTS.md NEVER 対応: ディレクトリ作成失敗を握りつぶさない
                    Console.Error.WriteLine($"[RemoteDedicatedLog] logsディレクトリ作成失敗: {ex.Message}");
                }
                _initialized = true;
            }
            Event.Cleanup(); // 起動時: 保持期間外のファイルを削除
            Controller.Cleanup();
        }

        /// <summary> Init 未呼び出し時のセーフネット（冪等） </summary>
        private static void EnsureInitialized()
        {
            if (!_initialized) Init();
        }

        /// <summary> 1行追記（スレッドセーフ・追記のみ・UTF-8 BOMなし） </summary>
        public void Append(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            // [task14 Q9] サーバ側 ON/OFF スイッチ（§3.8）: OFF 時はイベントログ（Event）のみ記録停止。
            // 動作ログ（Controller）は §3.8.5 のとおり OFF 対象外（常に記録・調査不能防止）。
            // 設定変更は次回 Append から即時反映（メモリ内フラグ・§3.8.2）。
            if (_prefix == "remote_event_log" && !_logEnabled) return;
            EnsureInitialized();
            // [Major-2 修正] lock 範囲は最小: パス解決＋書き込みのみ。
            // クリーンアップは lock 外・バックグラウンドで実行（§5.4）。
            lock (_writeLock)
            {
                try
                {
                    string path = CurrentFileLocked();
                    using (var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                    using (var sw = new StreamWriter(fs, new UTF8Encoding(false)))
                    {
                        sw.WriteLine(line);
                    }
                }
                catch (Exception ex)
                {
                    // AGENTS.md NEVER 対応: 書き込み失敗をサイレントに握りつぶさない
                    Console.Error.WriteLine($"[RemoteDedicatedLog] append失敗: {ex.GetType().Name}: {ex.Message}");
                }
            }
            MaybeCleanup(); // lock 解放後・日付rollover時のみトリガ（バックグラウンド実行）
        }

        /// <summary> 日付連番ファイル名を決定（サイズ超過時は連番切り替え・最大 MAX_SEQUENCE・Minor-S3） </summary>
        private string CurrentFileLocked()
        {
            string date = DateTime.Now.ToString("yyyyMMdd");
            string baseName = Path.Combine(_dir, _prefix + "_" + date);
            string path = baseName + ".log";
            if (!File.Exists(path) || new FileInfo(path).Length < _maxFileSize) return path;
            for (int i = 1; i <= MAX_SEQUENCE; i++)
            {
                string p = baseName + "_" + i + ".log";
                if (!File.Exists(p) || new FileInfo(p).Length < _maxFileSize) return p;
            }
            // [Minor-S3] 全連番ファイル満容量（異常）: 最後の連番に追記継続＋警告1回（無限ループしない）
            if (!_seqOverflowWarned)
            {
                _seqOverflowWarned = true;
                Console.Error.WriteLine($"[RemoteDedicatedLog] {date} の連番 {MAX_SEQUENCE} 個が満容量、最後のファイルに追記継続");
            }
            return baseName + "_" + MAX_SEQUENCE + ".log";
        }

        /// <summary>
        /// クリーンアップトリガ（日付rollover時のみ・1日1回）。
        /// [Major-2 修正] Directory.GetFiles / File.Delete を書き込みロック内で実行しない。
        /// 削除対象は「今日 - 保持期間」より古いファイルであり、書き込み中の今日ファイルとは
        /// 重複しないため、バックグラウンドスレッドでロックなし実行しても安全（§5.4）。
        /// </summary>
        private void MaybeCleanup()
        {
            var today = DateTime.Now.Date;
            if (_lastCleanupDate == today) return;
            lock (_initLock)
            {
                if (_lastCleanupDate == today) return;
                _lastCleanupDate = today;
            }
            try
            {
                Task.Run(() =>
                {
                    try
                    {
                        var cutoff = DateTime.Now.Date.AddDays(-_retentionDays);
                        foreach (var f in Directory.GetFiles(_dir, _prefix + "_*.log"))
                        {
                            try
                            {
                                if (File.GetLastWriteTime(f) < cutoff) File.Delete(f);
                            }
                            catch (Exception ex)
                            {
                                // AGENTS.md NEVER 対応: 個別ファイル削除失敗を記録（次回rolloverで再試行）
                                Console.Error.WriteLine($"[RemoteDedicatedLog] ファイル削除失敗: {f}: {ex.Message}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // クリーンアップ全体失敗: 次回の書き込みで再トリガされるよう日付をリセット
                        Console.Error.WriteLine($"[RemoteDedicatedLog] cleanup失敗: {ex.Message}");
                        lock (_initLock) { _lastCleanupDate = DateTime.MinValue; }
                    }
                });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[RemoteDedicatedLog] cleanupスケジューリング失敗: {ex.Message}");
                lock (_initLock) { _lastCleanupDate = DateTime.MinValue; }
            }
        }

        /// <summary> 保持期間以前のファイルを削除（起動時・日付rollover時・バックグラウンド実行） </summary>
        public void Cleanup()
        {
            EnsureInitialized();
            MaybeCleanupForce();
        }

        private void MaybeCleanupForce()
        {
            // 起動時呼び出し用: 日付判定に関わらずバックグラウンド実行
            try
            {
                Task.Run(() =>
                {
                    try
                    {
                        var cutoff = DateTime.Now.Date.AddDays(-_retentionDays);
                        foreach (var f in Directory.GetFiles(_dir, _prefix + "_*.log"))
                        {
                            try
                            {
                                if (File.GetLastWriteTime(f) < cutoff) File.Delete(f);
                            }
                            catch (Exception ex)
                            {
                                Console.Error.WriteLine($"[RemoteDedicatedLog] ファイル削除失敗: {f}: {ex.Message}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[RemoteDedicatedLog] cleanup失敗: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[RemoteDedicatedLog] cleanupスケジューリング失敗: {ex.Message}");
            }
        }
    }
}
