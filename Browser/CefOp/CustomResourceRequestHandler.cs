using CefSharp;
using CefSharp.Handler;
using System;
using System.Collections.Concurrent;   // [task21] 追加
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Browser.CefOp
{
	public class CustomResourceRequestHandler : ResourceRequestHandler
	{
		// [task21 v1.2] ログアウト（空白ページ）HTML 配信エントリ（named struct、v1.2 m-1 推奨）。
		// Bytes = HTML byte 列（UTF-8・BOM なし）、InsertedTicks = 挿入時刻（DateTime.Now.Ticks）。
		// 可視性は internal: 型は本アセンブリ内の LogoutHtmlStore（internal static）のみが参照するため、
		// C# のアクセシビリティ一貫性規則（CS0052: フィールド型はフィールド可視性以下）を満たす。
		internal struct LogoutHtmlEntry
		{
			public byte[] Bytes;
			public long InsertedTicks;
		}

		// [task21] ログアウト（空白ページ）HTML 配信ストア。
		// キー: 配信対象の完全 URL（ts 付き・一意）。
		// ハンドラは 1 リクエストごと new されるため static で共有する。
		// CEF IO スレッド（GetResourceHandler）と UI スレッド（RegisterLogoutHtml / ClearLogoutHtmlStore）
		// から参照されるため、ConcurrentDictionary でスレッド安全を保証する。
		// エントリ上限（LogoutHtmlStoreMaxCount）超過時は最旧（InsertedTicks 最小）エントリを削除し、
		// 無制限蓄積（メモリリーク）を防止する。
		// [task21 v1.4] スレッドセーフティ注記: ConcurrentDictionary のパブリックメンバーはスレッド安全だが、
		// LINQ 拡張メソッド（OrderBy / FirstOrDefault 等）は IEnumerable<T> 経由で呼び出されるため
		// スレッドセーフさが保証されない（Microsoft 公式ドキュメント）。上限制御では ToArray()
		// スナップショット取得後に LINQ を適用する（コードレビュー二次 Major 1 対応、§4.2 注記参照）。
		internal static readonly ConcurrentDictionary<string, LogoutHtmlEntry> LogoutHtmlStore =
			new ConcurrentDictionary<string, LogoutHtmlEntry>(StringComparer.OrdinalIgnoreCase);

		// [task21] ストアのエントリ上限。
		// 1 回ログアウトで 1 エントリ（約 2KB）。上限 8 で 16KB 程度に抑制し、
		// 直近の ts（F5 再読み込み）は維持する。
		private const int LogoutHtmlStoreMaxCount = 8;

		// [task21] ログアウト用 HTML を URL キーで登録する（FormBrowser.LogoutToBlankPage が呼ぶ）。
		// UI スレッド呼び出し。同一 URL の再登録は上書き。
		// 登録後、エントリが上限（LogoutHtmlStoreMaxCount）を超えた場合は
		// 最旧（InsertedTicks が最小）のエントリを削除する。
		// [task21 v1.4] 引数 null チェック（コードレビュー二次 Minor 1 対応）:
		// url が null / 空 の場合 LogoutHtmlStore[url] で ArgumentNullException を送出、
		// htmlBytes が null の場合 null エントリが登録されるため、警告ログを出力して早期 return。
		// サイレントな例外の握りつぶしはしない（AGENTS.md NEVER 準拠・空 catch なし）。
		internal static void RegisterLogoutHtml(string url, byte[] htmlBytes)
		{
			if (string.IsNullOrEmpty(url))
			{
				System.Diagnostics.Trace.WriteLine("[task21] RegisterLogoutHtml: url が null または空のため登録をスキップ");
				return;
			}
			if (htmlBytes == null)
			{
				System.Diagnostics.Trace.WriteLine("[task21] RegisterLogoutHtml: htmlBytes が null のため登録をスキップ。Url=" + url);
				return;
			}
			LogoutHtmlStore[url] = new LogoutHtmlEntry { Bytes = htmlBytes, InsertedTicks = DateTime.Now.Ticks };
			// 上限制御: 超過時は最旧エントリを削除。
			// [task21 v1.4] ConcurrentDictionary に対する LINQ 拡張メソッド（OrderBy / FirstOrDefault）は
			// IEnumerable<T> 経由で呼び出されるためスレッドセーフではない（Microsoft 公式ドキュメント:
			// インターフェース経由でアクセスされるメンバー（拡張メソッドを含む）はスレッドセーフさが
			// 保証されない）。本メソッドは UI スレッド、GetResourceHandler は CEF IO スレッドで
			// 実行されるため、イテレーション中の IO スレッド読み出しとの競合で ArgumentException の
			// リスクがある（コードレビュー二次 Major 1 対応）。
			// したがって ToArray() でスナップショット取得後に LINQ を適用する（ToArray は
			// ConcurrentDictionary のスレッドセーフなパブリックメンバー）。
			// スナップショット取得と TryRemove の間に他スレッドで削除される可能性があるため、
			// TryRemove の戻り値を確認し失敗しても例外を出さない（安全側: 削除漏れは
			// 次の登録時に再評価されるため、上限超過は次回登録時に解消される）。
			if (LogoutHtmlStore.Count > LogoutHtmlStoreMaxCount)
			{
				var oldest = LogoutHtmlStore
					.ToArray()
					.OrderBy(p => p.Value.InsertedTicks)
					.FirstOrDefault();
				if (!string.IsNullOrEmpty(oldest.Key))
				{
					LogoutHtmlEntry removed;
					if (!LogoutHtmlStore.TryRemove(oldest.Key, out removed))
					{
						// スナップショット取得後に他スレッドで削除済みの場合（稀）。
						// 削除漏れは次の登録時に再評価されるため、ここでは警告のみ。
						System.Diagnostics.Trace.WriteLine("[task21] RegisterLogoutHtml: 最旧エントリ削除失敗（既に削除済みの可能性）。Url=" + oldest.Key);
					}
				}
			}
		}

		// [task21] ストアを全クリアする（FormBrowser.Exit() が Cef.Shutdown() 前に呼ぶ）。
		// UI スレッド呼び出し。プロセス終了時に全エントリを解放し、
		// static ストアの残留を排除する。冪等（空・複数回の呼び出しでも例外なし）。
		// [task21 v1.4] スレッドコンテキスト注記（コードレビュー二次 Minor 3 対応）:
		// 本メソッドは UI スレッドから呼び出される（FormBrowser.Exit() の Cef.Shutdown() 直前）。
		// Cef.Shutdown() は IO スレッドの停止を同期的に待ち、リターン後には IO スレッドから
		// GetResourceHandler が呼ばれることはないため、Clear() と IO スレッドの読み出しが
		// 同時実行される可能性は実質ない。仮に同時実行されても ConcurrentDictionary.Clear() は
		// スレッドセーフ・冪等であるため例外は発生しない。
		internal static void ClearLogoutHtmlStore()
		{
			LogoutHtmlStore.Clear();
		}

		// ダミー映像（検証用映像源）配用の URL 基底。ログアウト空白ページと同一方式:
		// 実在しないポート 65535 + メモリ配信のため実ネットワーク接続は発生しない。
		// 127.0.0.1 は Chromium の secure context（potentially trustworthy）かつプロキシ暗黙バイパス対象であり、
		// WCS（VideoEncoder）が game と同じく利用可能。
		internal const string DummyVideoUrlBase = "http://127.0.0.1:65535/task29-dummy-video";

		// ダミー映像ページの厳密判定（IsLogoutBlankUrl と同一の Uri パース方式:
		// host 完全一致 + port 完全一致 + path 先頭一致。サブストリング/ワイルドカード一致は
		// 外部ページがキャプチャ源に偽装する誤検知を防げないため使用しない）。
		// クエリ（?mode=... 等）は AbsolutePath に含まれないため、バリアント URL も同判定で吸収される。
		internal static bool IsDummyVideoUrl(string url)
		{
			Uri uri;
			if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
				return false;
			return uri.Host == "127.0.0.1"
				&& uri.Port == 65535
				&& uri.AbsolutePath.StartsWith("/task29-dummy-video", StringComparison.Ordinal);
		}

		// ダミー映像 HTML（単一エントリ）。登録後 byte[] は不変として扱う（参照のスワップのみ volatile）。
		// スレッド安全の根拠: C# の参照の読み書きはアトミック（C# 言語仕様「変数参照のアトミック性」）のため、
		// CEF IO スレッド（GetResourceHandler）からは null（未登録/停止済み）または最新の byte[]（登録済み）の
		// いずれかが読まれ、中途半端な状態は発生しない。書き込みは UI スレッド
		//（RegisterDummyVideoHtml / ClearDummyVideoHtml）に限定される。
		private static volatile byte[] _dummyVideoHtml;

		// ダミー映像 HTML を登録する（FormBrowser.StartDummyVideo が呼ぶ。UI スレッド呼び出し）。
		// null は登録不能のため、サイレントスルーせず Trace に記録してスキップする。
		internal static void RegisterDummyVideoHtml(byte[] htmlBytes)
		{
			if (htmlBytes == null)
			{
				System.Diagnostics.Trace.WriteLine("[DummyVideo] RegisterDummyVideoHtml: htmlBytes が null のため登録をスキップ");
				return;
			}
			_dummyVideoHtml = htmlBytes;
		}

		// ダミー映像 HTML を解放する（FormBrowser.StopDummyVideo と FormBrowser.Exit の 2 経路が呼ぶ。
		// UI スレッド呼び出し）。冪等（null 代入のため複数回の呼び出しでも例外なし）。
		internal static void ClearDummyVideoHtml()
		{
			_dummyVideoHtml = null;
		}

		bool pixiSettingEnabled;

		public CustomResourceRequestHandler(bool pixiSettingEnabled) : base()
		{
			this.pixiSettingEnabled = pixiSettingEnabled;
		}


		/// <summary>
		/// レスポンスの置換制御を行います。
		/// </summary>
		protected override IResponseFilter GetResourceResponseFilter(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IRequest request, IResponse response)
		{
			if (pixiSettingEnabled && request.Url.Contains(@"/kcs2/index.php"))
				return new ResponseFilterPixiSetting();

			return base.GetResourceResponseFilter(chromiumWebBrowser, browser, frame, request, response);
		}

		/// <summary>
		/// 特定の通信をブロックします。
		/// </summary>
		protected override CefReturnValue OnBeforeResourceLoad(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IRequest request, IRequestCallback callback)
		{
			// ログイン直後に勝手に遷移させられ、ブラウザがホワイトアウトすることがあるためブロックする
			if (request.Url.Contains(@"/rt.gsspat.jp/"))
			{
				return CefReturnValue.Cancel;
			}

			return base.OnBeforeResourceLoad(chromiumWebBrowser, browser, frame, request, callback);
		}

		/// <summary>
		/// [task21] ログアウト（空白ページ）HTML をメモリから配信する。
		/// CustomRequestHandler が catch-all で全リクエストに本ハンドラを返すため、
		/// 本メソッドは必ず呼ばれる（CEF 解決順序に依存しない、task21 §2.3 / §3.3）。
		/// 登録済み URL に一致する場合のみ HTML を配信し、それ以外は base（null → 既定の
		/// ネットワーク読み込み）を返して既存挙動を維持する。
		/// </summary>
		/// <remarks>
		/// v1.1: CEF IO スレッドで呼ばれる（ResourceRequestHandler.GetResourceHandler の
		/// 公式リマーク「Called on the CEF IO thread」で確認）。UI スレッドの
		/// RegisterLogoutHtml との TOCTOU については §5.1（v1.1 追記）を参照。
		/// </remarks>
		protected override IResourceHandler GetResourceHandler(IWebBrowser chromiumWebBrowser, IBrowser browser, IFrame frame, IRequest request)
		{
			if (request != null && !string.IsNullOrEmpty(request.Url))
			{
				LogoutHtmlEntry entry;
				if (LogoutHtmlStore.TryGetValue(request.Url, out entry))
				{
					// [task21 v1.4] entry.Bytes null チェック（コードレビュー二次 Minor 2 対応）:
					// null の場合 ResourceHandler.FromByteArray で ArgumentNullException を送出するため、
					// 未登録扱いと同様に警告ログを出力し base（既定読み込み）へフォールバック。
					// 本来は RegisterLogoutHtml の引数 null チェックで null エントリが登録されないため、
					// ここは防御的ガード。
					if (entry.Bytes == null)
					{
						System.Diagnostics.Trace.WriteLine(string.Format(
							"[task21] 空白ページ HTML 配信: Bytes が null のため base（既定読み込み）へ。Url={0}", request.Url));
						return base.GetResourceHandler(chromiumWebBrowser, browser, frame, request);
					}
					// メモリ配信（ネットワーク接続なし）。UTF-8・BOM なし。
					// HTML は <meta charset="utf-8" /> を持つため文字化けしない
					//（FromByteArray の charSet 引数は 143.0.90 では実効性なし、§3.4.2 参照）。
					// oneTimeUse にせず登録は維持（S-1: F5 再読み込み対応を優先）。
					return ResourceHandler.FromByteArray(entry.Bytes, ResourceHandler.DefaultMimeType, "utf-8");
				}
				// [task21 v1.1] 配信失敗ログ（一次 Minor 2 対応: デバッグ性向上）。
				// GetResourceHandler は全リクエスト（画像・CSS・JS 等、CefSharp 公式 doc
				// 「Called on the CEF IO thread before a resource is loaded」で確認）で呼ばれるため、
				// 全 URL に対して警告を出すことは不可（ログ洪水）。
				// したがって、空白ページ配信用 URL パターン（"task20-logout-blank"）に一致する
				// 未登録リクエストのみ警告出力する（配信ミスの検知）。
				if (request.Url.Contains("task20-logout-blank"))
				{
					System.Diagnostics.Trace.WriteLine(string.Format(
						"[task21] 空白ページ HTML 配信ミスマッチ: URL が未登録のため base（既定読み込み）へ。Url={0}", request.Url));
				}
				// ダミー映像 HTML の配信（パス厳密判定・クエリバリアント対応）。
				// volatile 読み込みをローカル変数へ退避してから判定・配信する
				//（判定と配信の間に UI スレッドの Clear が走っても、取得済み byte[] で整合配信できる）。
				byte[] dummyBytes = _dummyVideoHtml;
				if (IsDummyVideoUrl(request.Url))
				{
					if (dummyBytes != null)
						return ResourceHandler.FromByteArray(dummyBytes, ResourceHandler.DefaultMimeType, "utf-8");
					// 未登録（停止済み）のダミー URL へのアクセスは配信ミス検知のため警告する
					//（本メソッドは全リクエストで呼ばれるため、配信パターン一致 URL のみに限定しログ洪水を防ぐ）。
					System.Diagnostics.Trace.WriteLine(string.Format(
						"[DummyVideo] ダミー映像 HTML 配信ミスマッチ: URL が未登録のため base（既定読み込み）へ。Url={0}", request.Url));
				}
			}
			return base.GetResourceHandler(chromiumWebBrowser, browser, frame, request);
		}

	}
}
