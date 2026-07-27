//============================================================================================
// system name    : ElectronicObserver
// program name   : DialogConfigurationRemote
// file name      : DialogConfigurationRemote.cs
// summary        : 遠隔操作サーバー詳細設定ダイアログ
//--------------------------------------------------------------------------------------------
// author         : myi
// creation date  : 2026/06/10
// version        : 1.00
//--------------------------------------------------------------------------------------------
#region "change history"
// change history : 2026/06/10 myi 新規作成
#endregion
//============================================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Windows.Forms;

namespace ElectronicObserver.Window.Dialog
{
	/// <summary>
	/// 遠隔操作サーバー（RemoteController）のポート番号やリッスンIPアドレスを設定するダイアログクラス。
	/// デザイナーを使わず、コードでレイアウトを構築します。
	/// </summary>
	public class DialogConfigurationRemote : Form
	{
		private Label labelPort;
		private NumericUpDown numericUpDownPort;
		private Label labelAddresses;
		private CheckedListBox checkedListBoxAddresses;
		private TextBox textBoxCustomAddress;
		private Button buttonAdd;
		private Button buttonDelete;
		// 2026/07/14 myi 追加（パスワード設定用コントロール）
		private Label labelPassword;
		private TextBox textBoxPassword;
		private Label labelNote;
		private Button buttonOK;
		private Button buttonCancel;

		// プリセットとして定義する基本アドレス一覧
		private readonly string[] PresetAddresses = { "*", "127.0.0.1", "::1" };

		/// <summary>
		/// ダイアログで確定されたポート番号
		/// </summary>
		public int ResultPort { get; private set; }

		/// <summary>
		/// ダイアログで確定されたセミコロン区切りのリッスンアドレス文字列
		/// </summary>
		public string ResultAddress { get; private set; }

		/// <summary>
		/// 2026/07/14 myi 追加（ダイアログで確定されたパスワード）
		/// </summary>
		public string ResultPassword { get; private set; }

		/// <summary>
		/// 遠隔操作設定ダイアログを初期化します。
		/// </summary>
		/// <param name="currentPort">現在の設定値（ポート番号）</param>
		/// <param name="currentAddress">現在の設定値（セミコロン区切りアドレス一覧）</param>
		/// <param name="currentPassword">2026/07/14 myi 追加（現在の設定値（パスワード））</param>
		public DialogConfigurationRemote(int currentPort, string currentAddress, string currentPassword)
		{
			// UIコントロールの構築およびレイアウト設定
			InitializeControls();

			// データのロード処理
			LoadSettings(currentPort, currentAddress, currentPassword);
		}

		/// <summary>
		/// 各種コントロールを生成・配置し、フォームの基本属性を設定します。
		/// </summary>
		private void InitializeControls()
		{
			this.Text = "遠隔操作サーバー詳細設定";
			this.ClientSize = new System.Drawing.Size(384, 360);
			this.FormBorderStyle = FormBorderStyle.FixedDialog;
			this.MaximizeBox = false;
			this.MinimizeBox = false;
			this.ShowInTaskbar = false;
			this.StartPosition = FormStartPosition.CenterParent;

			// ポート設定ラベル
			labelPort = new Label
			{
				Text = "リッスンポート番号 * :",
				Location = new System.Drawing.Point(12, 18),
				Size = new System.Drawing.Size(130, 15)
			};

			// ポート設定NumericUpDown
			numericUpDownPort = new NumericUpDown
			{
				Location = new System.Drawing.Point(150, 15),
				Size = new System.Drawing.Size(80, 23),
				Minimum = 1,
				Maximum = 65535,
				TextAlign = HorizontalAlignment.Right
			};

			// 2026/07/14 myi 追加（パスワード設定ラベル）
			labelPassword = new Label
			{
				Text = "パスワード :",
				Location = new System.Drawing.Point(12, 48),
				Size = new System.Drawing.Size(130, 15)
			};

			// 2026/07/14 myi 追加（パスワード設定TextBox）
			textBoxPassword = new TextBox
			{
				Location = new System.Drawing.Point(150, 45),
				Size = new System.Drawing.Size(180, 23),
				UseSystemPasswordChar = true
			};

			// リッスンIPリスト用ラベル
			labelAddresses = new Label
			{
				Text = "リッスンIPアドレス一覧（複数選択可） :",
				Location = new System.Drawing.Point(12, 78),
				Size = new System.Drawing.Size(300, 15)
			};

			// リッスンIP用CheckedListBox
			checkedListBoxAddresses = new CheckedListBox
			{
				Location = new System.Drawing.Point(12, 100),
				Size = new System.Drawing.Size(360, 120),
				CheckOnClick = true
			};

			// カスタムIP入力用TextBox
			textBoxCustomAddress = new TextBox
			{
				Location = new System.Drawing.Point(12, 232),
				Size = new System.Drawing.Size(180, 23)
			};

			// カスタムIP追加ボタン
			buttonAdd = new Button
			{
				Text = "追加",
				Location = new System.Drawing.Point(200, 231),
				Size = new System.Drawing.Size(80, 25),
				UseVisualStyleBackColor = true
			};
			buttonAdd.Click += ButtonAdd_Click;

			// カスタムIP削除ボタン
			buttonDelete = new Button
			{
				Text = "削除",
				Location = new System.Drawing.Point(285, 231),
				Size = new System.Drawing.Size(87, 25),
				UseVisualStyleBackColor = true
			};
			buttonDelete.Click += ButtonDelete_Click;

			// 注意書きラベル
			labelNote = new Label
			{
				Text = "* 変更を適用するには本ツール（本体）を再起動してください。",
				Location = new System.Drawing.Point(12, 270),
				Size = new System.Drawing.Size(360, 30),
				ForeColor = System.Drawing.Color.Red
			};

			// OKボタン
			buttonOK = new Button
			{
				Text = "OK",
				Location = new System.Drawing.Point(210, 310),
				Size = new System.Drawing.Size(80, 25),
				UseVisualStyleBackColor = true
			};
			buttonOK.Click += ButtonOK_Click;

			// キャンセルボタン
			buttonCancel = new Button
			{
				Text = "キャンセル",
				Location = new System.Drawing.Point(295, 310),
				Size = new System.Drawing.Size(80, 25),
				UseVisualStyleBackColor = true
			};
			buttonCancel.Click += ButtonCancel_Click;

			// フォームに各コントロールを追加
			this.Controls.Add(labelPort);
			this.Controls.Add(numericUpDownPort);
			this.Controls.Add(labelPassword);
			this.Controls.Add(textBoxPassword);
			this.Controls.Add(labelAddresses);
			this.Controls.Add(checkedListBoxAddresses);
			this.Controls.Add(textBoxCustomAddress);
			this.Controls.Add(buttonAdd);
			this.Controls.Add(buttonDelete);
			this.Controls.Add(labelNote);
			this.Controls.Add(buttonOK);
			this.Controls.Add(buttonCancel);

			// デフォルトボタン（Enter/Escape）のバインド
			this.AcceptButton = buttonOK;
			this.CancelButton = buttonCancel;
		}

		/// <summary>
		/// 現在の設定情報をコントロールに適用します。
		/// </summary>
		/// <param name="port">ポート番号</param>
		/// <param name="addressString">セミコロン区切りアドレス一覧</param>
		/// <param name="password">2026/07/14 myi 追加（パスワード）</param>
		private void LoadSettings(int port, string addressString, string password)
		{
			numericUpDownPort.Value = Math.Max(1, Math.Min(65535, port));

			// 2026/07/14 myi 追加（パスワードの読み込み）
			textBoxPassword.Text = password ?? "";

			// 設定されているリッスンアドレスを切り分け
			var configuredAddresses = (addressString ?? "")
				.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
				.Select(a => a.Trim())
				.ToList();

			// まずプリセット項目をリストへ追加し、設定に含まれていればチェックを入れる
			foreach (var preset in PresetAddresses)
			{
				bool isChecked = configuredAddresses.Contains(preset);
				checkedListBoxAddresses.Items.Add(preset, isChecked);
			}

			// プリセット以外のカスタム指定アドレスをリストへ追加してチェックを入れる
			foreach (var addr in configuredAddresses)
			{
				if (!PresetAddresses.Contains(addr))
				{
					checkedListBoxAddresses.Items.Add(addr, true);
				}
			}
		}

		/// <summary>
		/// カスタムIPアドレスをリストに追加します。
		/// </summary>
		private void ButtonAdd_Click(object sender, EventArgs e)
		{
			string newAddress = (textBoxCustomAddress.Text ?? "").Trim();
			if (string.IsNullOrEmpty(newAddress)) return;

			// リスト内に既に存在していないか重複をチェック
			if (checkedListBoxAddresses.Items.Cast<object>().Any(item => item.ToString() == newAddress))
			{
				MessageBox.Show("指定されたアドレスは既にリストに存在します。", "追加エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
				return;
			}

			// リストに追加してデフォルトでチェック状態にする
			checkedListBoxAddresses.Items.Add(newAddress, true);
			textBoxCustomAddress.Clear();
		}

		/// <summary>
		/// 選択されたカスタムIPアドレスをリストから削除します。
		/// </summary>
		private void ButtonDelete_Click(object sender, EventArgs e)
		{
			int selectedIndex = checkedListBoxAddresses.SelectedIndex;
			if (selectedIndex == -1) return;

			string selectedValue = checkedListBoxAddresses.Items[selectedIndex].ToString();

			// プリセット項目は安全性の観点から削除不可とする
			if (PresetAddresses.Contains(selectedValue))
			{
				MessageBox.Show("プリセット項目（*, 127.0.0.1, ::1）は削除できません。不要な場合はチェックを外してください。", "削除制限", MessageBoxButtons.OK, MessageBoxIcon.Information);
				return;
			}

			// カスタム項目の削除実行
			checkedListBoxAddresses.Items.RemoveAt(selectedIndex);
		}

		/// <summary>
		/// 設定を確定してダイアログを閉じます。
		/// </summary>
		private void ButtonOK_Click(object sender, EventArgs e)
		{
			// チェックされている全リッスンターゲットをセミコロン（;）で連結
			var checkedItems = checkedListBoxAddresses.CheckedItems.Cast<object>().Select(o => o.ToString()).ToList();
			if (checkedItems.Count == 0)
			{
				MessageBox.Show("少なくとも1つのリッスンIPアドレスにチェックを入れてください。", "保存エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
				return;
			}

			ResultPort = (int)numericUpDownPort.Value;
			ResultAddress = string.Join(";", checkedItems);

			// 2026/07/14 myi 追加（パスワードの確定）
			ResultPassword = textBoxPassword.Text ?? "";

			this.DialogResult = DialogResult.OK;
			this.Close();
		}

		/// <summary>
		/// 変更を破棄してダイアログを閉じます。
		/// </summary>
		private void ButtonCancel_Click(object sender, EventArgs e)
		{
			this.DialogResult = DialogResult.Cancel;
			this.Close();
		}
	}
}
