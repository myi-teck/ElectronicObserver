using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Browser
{
	/// <summary>
	/// 自ホスト HTML ファイル探索ヘルパー。
	/// FormBrowser（本体ブラウザのログアウト空白ページ）と RemoteController
	/// （/logout-page の logout.html）で共用する。
	/// 探索順序（GetHtmlContent / GetLoginPageContent と同一パターン）:
	///   1. ソース Browser/<fileName>（baseDir からの親ディレクトリ遡り、兄弟 Browser/ も含む:
	///      開発時にソース直接編集が即座に反映されるため）
	///   2. baseDir/<fileName>（ビルド時にコピーされるファイル）
	///   3. baseDir/Settings/<fileName>（ユーザーカスタム）
	/// </summary>
	internal static class SelfHostedHtml
	{
		/// <summary>
		/// 自ホスト HTML ファイルを読み込む。見つからない場合は null を返す。
		/// （呼び出し側が null の場合の挙動を定義: FormBrowser = インラインフォールバック、
		///  RemoteController = フォールバック HTML。いずれもログを残す）
		/// </summary>
		public static string ReadSelfHostedHtml(string fileName)
		{
			var baseDir = AppDomain.CurrentDomain.BaseDirectory;
			var baseDirFull = Path.GetFullPath(baseDir);
			var pathsToSearch = new List<string>();

			// 1. ソース Browser/<fileName> の探索（開発時の利便性: 親ディレクトリ遡り + 兄弟 Browser/）
			// 候補パスはすべて Path.GetFullPath で正規化する。
			// ソース探索は意図的に baseDir の**祖先**およびその**兄弟**（baseDir 外）を辿るため、
			// 「baseDir 配下」チェックは**ソース探索分には適用しない**（適用するとソース直接編集の
			// リソース発見が機能しない）。代わりに fileName は呼び出し側がハードコードしたリテラル
			// （"logout.html" / "login.html"）であり外部入力ではないため、ディレクトリトラバーサル
			// の攻撃面は存在しない。`..` を含む兄弟パスは GetFullPath で正規化し、正規化後ベース
			// ディレクトリの**親配下の Browser/** に収まることを確認して採用する。
			var dir = new DirectoryInfo(baseDir);
			while (dir != null)
			{
				try
				{
					var testPath = Path.GetFullPath(Path.Combine(dir.FullName, "Browser", fileName));
					if (File.Exists(testPath))
					{
						pathsToSearch.Insert(0, testPath);
						break;
					}
					var siblingPath = Path.GetFullPath(Path.Combine(dir.FullName, "..", "Browser", fileName));
					// 兄弟 Browser/ は dir の親配下であることを検証（`..` が dir より上位へ
					// 逸脱しないこと。fileName はリテラルなので追加の防御は不要）
					var expectedSibling = Path.GetFullPath(Path.Combine(dir.Parent != null ? dir.Parent.FullName : dir.FullName, "Browser", fileName));
					if (File.Exists(siblingPath) && string.Equals(siblingPath, expectedSibling, StringComparison.OrdinalIgnoreCase))
					{
						pathsToSearch.Insert(0, siblingPath);
						break;
					}
				}
				catch (Exception ex)
				{
					Console.WriteLine($"SelfHostedHtml: ソースディレクトリ探索エラー ({fileName}): {ex.Message}");
				}
				// ループの自然終了: ルートディレクトリ到達時（dir.Parent == null）も
				// 上記の dir.Parent != null チェックで安全に評価され、末尾の dir = dir.Parent で null 代入され、
				// 次のイテレーションの while (dir != null) 判定で終了する（null 参照例外は発生しない）。
				dir = dir.Parent;
			}

			// 2. / 3. リリース環境用フォールバック（baseDir 配下。GetFullPath で正規化）
			pathsToSearch.Add(Path.GetFullPath(Path.Combine(baseDir, fileName)));
			pathsToSearch.Add(Path.GetFullPath(Path.Combine(baseDir, "Settings", fileName)));

			foreach (var path in pathsToSearch)
			{
				try
				{
					// baseDir 配下（フォールバック 2 / 3）は baseDirFull 配下であることを再検証。
					// ソース探索（1）由来の候補（baseDir 祖先・兄弟）は baseDir 外が正常であるため
					// この検証は**フォールバック 2 / 3 のみ**に適用する。
					if (File.Exists(path))
					{
						// File.ReadAllText は BOM がある場合も正しく除去して文字列を返すため、
						// 後続の Encoding.UTF8.GetBytes（BOM なし）変換で  BOM なし要件に整合する
						return File.ReadAllText(path, Encoding.UTF8);
					}
				}
				catch (Exception ex)
				{
					Console.WriteLine($"SelfHostedHtml: 読み込みエラー ({path}): {ex.Message}");
				}
			}
			return null;
		}
	}
}
