using ElectronicObserver.Observer;
using ElectronicObserver.Observer.kcsapi.api_get_member;
using ElectronicObserver.Observer.kcsapi.api_port;
using ElectronicObserver.Resource;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using WeifenLuo.WinFormsUI.Docking;
using static System.Windows.Forms.LinkLabel;

namespace ElectronicObserver.Window
{

	public partial class FormMemo : DockContent
	{
		private string f_memoValue;

		public FormMemo(FormMain parent)
		{
			// 初期化
			InitializeComponent();
			// コンフィグ再設定
			ConfigurationChanged();
			// フォーム終了時処理追加(memo内容保存用);
			Application.ApplicationExit += ExitForm;
		}

		/// <summary>
		/// フォームロード時処理
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		private void FormLog_Load(object sender, EventArgs e)
		{
			// イベント設定　コンフィグ変更時に設定を反映させてるっぽい
			Utility.Configuration.Instance.ConfigurationChanged += ConfigurationChanged;

			// アイコン設定
			Icon = ResourceManager.ImageToIcon(ResourceManager.Instance.Icons.Images[(int)ResourceManager.IconContent.FormEquipmentList]);

			// 前回値読出
			ReadText();
		}

		/// <summary>
		/// コンフィグ変更時処理
		/// </summary>
		void ConfigurationChanged()
		{
			// フォント反映
			txb_Memo.Font = Font = Utility.Configuration.Config.UI.MainFont;

			// テーマ反映
			Utility.ThemeManager.ApplyTheme(this);
		}

		/// <summary>
		/// よくわからん
		/// </summary>
		/// <returns></returns>
		protected override string GetPersistString()
		{
			return "Memo";
		}

		/// <summary>
		/// テキスト保存
		/// </summary>
		private void SaveText()
		{
			// パス設定
			string path = System.IO.Path.Combine(Application.StartupPath,"memo.txt");
			// パスにファイルが存在する場合のみ実行
			if (File.Exists(path)){
				// shiftJISで書き込み 上書き
				using(StreamWriter writer = new StreamWriter(path, false, Encoding.GetEncoding("UTF-8"))){
					// 書き込み
					writer.Write(f_memoValue);
				}
			}
		}

		/// <summary>
		/// テキスト読込
		/// </summary>
		private void ReadText()
		{
			// パス設定
			string path = System.IO.Path.Combine(Application.StartupPath,"memo.txt");
			// パスにファイルが存在する場合のみ実行
			if (File.Exists(path)){
				using(var wr = new StreamReader(path)){
					//　読込反映
					this.txb_Memo.Text = wr.ReadToEnd();
				}
			}

		}


		/// <summary>
		/// フォーム終了時処理
		/// </summary>
		private void ExitForm(object sender,EventArgs e)
		{
			// テキスト保存
			SaveText();
		}

		/// <summary>
		/// テキスト変更時処理
		/// </summary>
		/// <param name="sender"></param>
		/// <param name="e"></param>
		private void TextChange_txb_Memo(object sender, EventArgs e)
		{
			// memo内容を保持　保持用クラスがあっても良い？　
			// テキストボックス内のテキストはフォーム終了時イベントが発火するときには空になっているので、変数に退避させている
			f_memoValue = this.txb_Memo.Text;
		}
	}
}
