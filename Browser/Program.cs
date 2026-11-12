using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Browser
{
	static class Program
	{
		/// <summary>
		/// アプリケーションのメイン エントリ ポイントです。
		/// </summary>
		[STAThread]
		static void Main(string[] args)
		{
			// FormBrowserHostから起動された時は引数に通信用URLが渡される
			if (args.Length == 0)
			{
				MessageBox.Show("これは七四式電子観測儀のサブプログラムであり、単体では起動できません。\r\n本体から起動してください。",
					"情報", MessageBoxButtons.OK, MessageBoxIcon.Information);
				return;
			}
			Application.EnableVisualStyles();
			Application.SetCompatibleTextRenderingDefault(false);
			AppDomain.CurrentDomain.AssemblyResolve += CurrentDomain_AssemblyResolve;

			// CefSharpSettings.Proxy の既定値設定は本箇所に集約する。
			// Application.Run（FormBrowser コンストラクタ・型ロード）より前にプロキシを早期設定するため、
			// FormBrowser が CefSharp の自動初期化をトリガーする前に、
			// APIObserver の既定プロキシ（127.0.0.1:40620）を反映させる。
			// 後続の FormBrowser.InitializeBrowser では、ProxySettings が到着済み（SetProxy 経由）かつ
			// Cef.Initialize 実行前の場合のみ再適用される（同一値の重複設定を排除）。
			// Cef.Initialize が既に実行済みの場合、CefSharpSettings.Proxy の変更は無効なため
			// 上書きは行われない（この場合は FormBrowser 側で警告ログを出力）。
			FormBrowser.ApplyCefSharpProxy(FormBrowser.DefaultApiObserverHost + ":" + FormBrowser.DefaultApiObserverPort);

			Application.Run(new FormBrowser(args[0]));
		}

		private static System.Reflection.Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
		{
			if (args.Name.StartsWith("CefSharp"))
			{
				string asmname = args.Name.Split(",".ToCharArray(), 2)[0] + ".dll";
				string arch = System.IO.Path.Combine(AppDomain.CurrentDomain.SetupInformation.ApplicationBase, Environment.Is64BitProcess ? "x64" : "x86", asmname);

				if (!System.IO.File.Exists(arch))
					return null;

				try
				{
					return System.Reflection.Assembly.LoadFile(arch);
				}
				catch (IOException ex) when (ex is FileNotFoundException || ex is FileLoadException)
				{
					if (MessageBox.Show(
$@"ブラウザコンポーネントがロードできませんでした。動作に必要な
「Microsoft Visual C++ 再頒布可能パッケージ」
がインストールされていないのが原因の可能性があります。
ダウンロードページを開きますか？
(vc_redist.{(Environment.Is64BitProcess ? "x64" : "x86")}.exe をインストールしてください。)",
						"CefSharp ロードエラー", MessageBoxButtons.YesNo, MessageBoxIcon.Error, MessageBoxDefaultButton.Button1)
						== DialogResult.Yes)
					{
						System.Diagnostics.Process.Start(@"https://learn.microsoft.com/ja-JP/cpp/windows/latest-supported-vc-redist?view=msvc-170");
					}

					// なんにせよ今回は起動できないのであきらめる
					throw;
				}
				catch (NotSupportedException)
				{
					// 概ね ZoneID を外し忘れているのが原因

					if (MessageBox.Show(
@"ブラウザの起動に失敗しました。
zip のセキュリティ許可を行うなど、インストール手順を再確認してみてください。
インストールガイドを開きますか？（外部ブラウザが開きます）",
							"ブラウザロード失敗", MessageBoxButtons.YesNo, MessageBoxIcon.Error)
						== DialogResult.Yes)
						System.Diagnostics.Process.Start(@"https://github.com/andanteyk/ElectronicObserver/wiki/Install");

					// なんにせよ今回は起動できないのであきらめる
					throw;
				}
			}
			return null;
		}
	}
}
