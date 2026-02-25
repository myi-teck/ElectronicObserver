//============================================================================================
// system name    : ElectronicObserver
// program name   : ThemeManager
// file name      : ThemeManager.cs
// summary        : 配色統一管理クラス。イベントフックにより、動的なリセットにも自動対応します。
//--------------------------------------------------------------------------------------------
// author         : myi
// creation date  : 2026/02/25
// version        : 2.00
//--------------------------------------------------------------------------------------------
#region "change history"
// change history : 2026/02/25 myi 新規作成
// change history : 2026/02/25 myi XML形式への移行、DataGridViewの配色強化、背景色連動文字色の追加
// change history : 2026/02/25 myi カスタムコントロール（ShipStatusHP等）への個別対応を追加
// change history : 2026/02/25 myi グローバル設定 (Configuration.Config) への注入ロジックを追加
#endregion
//============================================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using System.Xml;
using System.Xml.Serialization;
using ElectronicObserver.Window;
using ElectronicObserver.Window.Control;

namespace ElectronicObserver.Utility
{
	/// <summary>
	/// アプリケーション全体の配色を管理し、コントロールに適用するクラスです。
	/// BackColorChanged イベントを監視し、SystemColors へのリセットを自動的にテーマ色で上書きします。
	/// これにより、既存の Form コードを一切変更せずにダークテーマを維持できます。
	/// </summary>
	public static class ThemeManager
	{
		// テーマ設定ファイルの相対パス
		private const string ConfigFilePath = @"Settings\ThemeConfiguration.xml";

		// --- テーマ色プロパティ ---
		// テーマが読み込まれていない場合はシステムデフォルト色を使用
		public static Color BackColor { get; private set; } = SystemColors.Control;
		public static Color ForeColor { get; private set; } = SystemColors.ControlText;
		public static Color SubBackColor { get; private set; } = SystemColors.Window;
		public static Color HighlightColor { get; private set; } = SystemColors.Highlight;
		public static Color GridColor { get; private set; } = SystemColors.ControlDark;
		public static FlatStyle ButtonStyle { get; private set; } = FlatStyle.Standard;

		// テーマが正常に読み込まれたかを示すフラグ
		private static bool _isLoaded = false;

		// イベントフック処理中かを示すフラグ（無限ループ防止用）
		// BackColor を変更すると BackColorChanged が発火し、そのハンドラ内で再び BackColor を変更すると
		// 無限ループに陥るため、このフラグで再入を防止する
		private static bool _isApplying = false;

		#region XML Serialization

		/// <summary>
		/// テーマ設定の1つ分を表すクラス（XML デシリアライズ用）
		/// </summary>
		[Serializable]
		public class ThemeSetting
		{
			[XmlAttribute("name")]
			public string Name { get; set; } = "";
			public string BackColor { get; set; } = "";
			public string ForeColor { get; set; } = "";
			public string SubBackColor { get; set; } = "";
			public string HighlightColor { get; set; } = "";
			public string GridColor { get; set; } = "";
			public string ButtonStyle { get; set; } = "";
		}

		/// <summary>
		/// テーマ設定ファイル全体を表すクラス（XML デシリアライズ用）
		/// </summary>
		[Serializable]
		[XmlRoot("ThemeConfiguration")]
		public class ThemeConfiguration
		{
			[XmlArray("Themes")]
			[XmlArrayItem("Theme")]
			public List<ThemeSetting> Themes { get; set; } = new List<ThemeSetting>();
			public string CurrentTheme { get; set; } = "";
		}

		#endregion

		#region テーマ読み込み

		/// <summary>
		/// 設定ファイル (ThemeConfiguration.xml) からテーマを読み込みます。
		/// 読み込みに成功した場合、グローバル設定 (Configuration.Config) への注入も行います。
		/// </summary>
		public static void Load()
		{
			try
			{
				// 実行ディレクトリからの相対パスで設定ファイルを探す
				string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ConfigFilePath);

				if (!File.Exists(path))
				{
					// 開発環境では bin\Debug の1つ上にあることがあるため代替パスも探す
					string altPath = Path.Combine(Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory).FullName, ConfigFilePath);
					if (File.Exists(altPath))
					{
						path = altPath;
					}
					else
					{
						Logger.Add(3, $"テーマ設定ファイルが見つかりません: {path}");
						return;
					}
				}

				// XML ファイルを読み込んでデシリアライズ
				using (var reader = XmlReader.Create(path))
				{
					var serializer = new XmlSerializer(typeof(ThemeConfiguration));
					if (serializer.Deserialize(reader) is ThemeConfiguration config)
					{
						// 現在選択されているテーマ名に一致する設定を探す
						var theme = config.Themes.FirstOrDefault(t => t.Name == config.CurrentTheme);
						if (theme != null)
						{
							// テーマの各色を HTML カラーコードからパース
							BackColor = ColorTranslator.FromHtml(theme.BackColor);
							ForeColor = ColorTranslator.FromHtml(theme.ForeColor);
							SubBackColor = ColorTranslator.FromHtml(theme.SubBackColor);
							HighlightColor = ColorTranslator.FromHtml(theme.HighlightColor);
							GridColor = ColorTranslator.FromHtml(theme.GridColor);

							// ボタンスタイルのパース（Flat, Standard 等）
							if (Enum.TryParse(theme.ButtonStyle, out FlatStyle style))
							{
								ButtonStyle = style;
							}

							_isLoaded = true;
							Logger.Add(2, $"テーマ「{config.CurrentTheme}」を読み込みました。");

							// Configuration.Config 内の色設定をテーマ色で上書き（メモリ上のみ）
							InjectToConfiguration();
						}
						else
						{
							Logger.Add(3, $"テーマ「{config.CurrentTheme}」が定義されていません。");
						}
					}
				}
			}
			catch (Exception ex)
			{
				Logger.Add(3, "テーマ設定の読み込み中にエラーが発生しました: " + ex.Message);
			}
		}

		#endregion

		#region グローバル設定への注入

		/// <summary>
		/// アプリケーション全体の共通設定 (Configuration.Config) に配色を注入します。
		/// HPバーの背景色や出撃エリアのデフォルト色など、設定ファイル経由で読み込まれる色を
		/// テーマ色で上書きすることで、既存の描画コードを変更せずにテーマを反映させます。
		/// ※ メモリ上の値を書き換えるだけで、設定ファイル自体は変更しません。
		/// </summary>
		private static void InjectToConfiguration()
		{
			var config = Configuration.Config;
			if (config == null) return;

			// HPバーのカラースキーム（インデックス 11 = 背景色）をテーマの GridColor に合わせる
			if (config.UI?.BarColorScheme != null && config.UI.BarColorScheme.Count > 11)
			{
				config.UI.BarColorScheme[11] = GridColor;
			}

			// 艦隊パネルの出撃エリア色（インデックス 0 = デフォルト背景）をテーマ背景色に合わせる
			if (config.FormFleet?.SallyAreaColorScheme != null && config.FormFleet.SallyAreaColorScheme.Count > 0)
			{
				config.FormFleet.SallyAreaColorScheme[0] = BackColor;
			}
		}

		#endregion

		#region テーマ適用（イベントフック方式）

		/// <summary>
		/// 指定したコントロールとその子コントロールにテーマを適用します。
		/// さらに、BackColorChanged イベントを監視して、SystemColors へのリセットが
		/// 発生した際に自動的にテーマ色を再適用するフックを登録します。
		/// また、ControlAdded イベントも監視し、動的に追加される子コントロールにも
		/// 自動的にテーマを適用します。
		/// </summary>
		/// <param name="control">適用対象のコントロール</param>
		public static void ApplyTheme(Control control)
		{
			if (control == null || !_isLoaded) return;

			// --- ステップ1: 現在の色をテーマ色に適用 ---
			ApplyToControl(control);

			// --- ステップ2: 背景色変更の監視フック（動的リセット対策） ---
			// SystemColors へのリセットをテーマ色に差し替え、
			// さらに明るい背景色（Moccasin等）に変わった際に文字色を自動調整する
			control.BackColorChanged -= OnBackColorChanged;
			control.BackColorChanged += OnBackColorChanged;

			// --- ステップ3: 文字色変更の監視フック ---
			// ダーク背景上で SystemColors.ControlText（黒）にリセットされた文字色を検知し、
			// テーマに合った文字色（白）に差し替える
			control.ForeColorChanged -= OnForeColorChanged;
			control.ForeColorChanged += OnForeColorChanged;

			// --- ステップ4: 動的に追加される子コントロールへの対応 ---
			control.ControlAdded -= OnControlAdded;
			control.ControlAdded += OnControlAdded;

			// --- ステップ5: 子コントロールに再帰的に適用 ---
			foreach (Control child in control.Controls)
			{
				ApplyTheme(child);
			}
		}

		/// <summary>
		/// BackColorChanged イベントハンドラ。
		/// (1) SystemColors（OS標準色）にリセットされた場合 → テーマ色で自動上書き
		/// (2) 明るい背景色（Moccasin, LightGreen 等）に変更された場合 → 文字色を黒に自動調整
		/// これにより、MVP表示や工廠完了点滅の際にも文字が読めるようになります。
		/// </summary>
		private static void OnBackColorChanged(object sender, EventArgs e)
		{
			// 既にテーマ適用中の場合は無限ループ防止のため何もしない
			if (_isApplying) return;

			var c = (Control)sender;

			try
			{
				_isApplying = true;

				// パターン1: SystemColors へのリセット → テーマ色で上書き
				if (c.BackColor == SystemColors.Control || c.BackColor == SystemColors.Window)
				{
					c.BackColor = BackColor;
					c.ForeColor = GetForeColor(c.BackColor);
				}
				else
				{
					// パターン2: 意図的な背景色変更（Moccasin, LightGreen 等）
					// → 背景色はそのまま維持し、文字色だけを視認性に基づいて自動調整する

					if (c.BackColor == Color.Transparent || c.BackColor == Color.Empty)
					{
						// 透明 = 親コントロールの背景色が実際に見えているので、
						// 親の色を基準に文字色を決定する
						// 例: 点滅で LightGreen → Transparent に戻った際、
						//      親のダーク背景を参照して白文字に復帰する
						Color effectiveBack = c.Parent?.BackColor ?? BackColor;
						c.ForeColor = GetForeColor(effectiveBack);
					}
					else
					{
						// 明るい背景色（Moccasin, LightGreen, Silver 等）
						// → 文字色を黒に自動調整
						c.ForeColor = GetForeColor(c.BackColor);
					}
				}

				// --- カスタムコントロール対応 ---
				// ShipStatusHP 等は標準の ForeColor ではなく、独自の MainFontColor で文字を描画する。
				// BackColor が変わった際に MainFontColor も同時に調整しないと、
				// 明るい背景（MVP の Moccasin 等）の上に白文字が残って読めなくなる。
				if (c is ShipStatusHP hp)
				{
					Color adjustedFore = GetForeColor(c.BackColor);
					hp.MainFontColor = adjustedFore;
					hp.SubFontColor = BlendColor(adjustedFore, c.BackColor, 0.4);
				}
			}
			finally
			{
				_isApplying = false;
			}
		}

		/// <summary>
		/// ForeColorChanged イベントハンドラ。
		/// ダーク背景上で SystemColors.ControlText（黒）に文字色がリセットされた場合に、
		/// テーマに合った文字色（白）に自動差し替えします。
		/// 戦闘パネルの航空戦ステージ数値や勝敗判定の文字色がこのパターンに該当します。
		/// ※ Color.Red（警告色）など意図的に設定された色は影響を受けません。
		/// </summary>
		private static void OnForeColorChanged(object sender, EventArgs e)
		{
			if (_isApplying) return;

			var c = (Control)sender;

			// SystemColors.ControlText（黒）に戻された場合のみ介入
			// Color.Red 等の意図的な文字色は SystemColors ではないため素通りする
			if (c.ForeColor == SystemColors.ControlText)
			{
				try
				{
					_isApplying = true;
					// 現在の背景色に応じた視認性の高い文字色に差し替え
					c.ForeColor = GetForeColor(c.BackColor);
				}
				finally
				{
					_isApplying = false;
				}
			}
		}

		/// <summary>
		/// ControlAdded イベントハンドラ。
		/// 親コントロールに新しい子コントロールが追加された際に、
		/// 自動的にテーマを適用し、イベントフックも登録します。
		/// </summary>
		private static void OnControlAdded(object sender, ControlEventArgs e)
		{
			// 新たに追加された子コントロールにもテーマを適用（再帰的にフックも登録される）
			ApplyTheme(e.Control);
		}

		#endregion

		#region コントロール種別ごとの適用ロジック

		/// <summary>
		/// コントロールの種別に応じた配色適用を行います。
		/// Form, Panel, Label, TextBox, DataGridView 等、種別ごとに最適な処理を実行します。
		/// </summary>
		private static void ApplyToControl(Control control)
		{
			// --- コンテナ系: Form, Panel, UserControl, TableLayoutPanel 等 ---
			if (control is Form || control is Panel || control is UserControl ||
				control is TableLayoutPanel || control is SplitContainer ||
				control is TabControl || control is TabPage)
			{
				// 背景色がシステム色または透明の場合のみテーマ色で上書き
				if (control.BackColor == SystemColors.Control ||
					control.BackColor == SystemColors.Window ||
					control.BackColor == Color.Transparent ||
					control.BackColor == Color.Empty)
				{
					control.BackColor = BackColor;
				}
				// 文字色は背景色に合わせて自動調整（白または黒）
				control.ForeColor = GetForeColor(control.BackColor);

				// FormFleet が持つ独自の配色プロパティをテーマに合わせる
				if (control is FormFleet fleet)
				{
					fleet.MainFontColor = ForeColor;
					fleet.SubFontColor = BlendColor(ForeColor, BackColor, 0.4);
				}

				// --- カスタムコントロールへの個別対応 ---
				// 独自の OnPaint で描画しているコントロールは、BackColor だけでなく
				// 内部の描画用プロパティも直接テーマ色に設定する必要がある
				if (control is ShipStatusHP hp)
				{
					// HPバーの文字色とバー背景色をテーマに合わせる
					hp.MainFontColor = GetForeColor(hp.BackColor);
					hp.SubFontColor = BlendColor(hp.MainFontColor, hp.BackColor, 0.4);
					hp.RepairFontColor = HighlightColor;
					ApplyStatusBarTheme(hp.HPBar);
				}
				else if (control is ShipStatusLevel level)
				{
					// レベル表示の文字色をテーマに合わせる
					level.MainFontColor = GetForeColor(level.BackColor);
					level.SubFontColor = BlendColor(level.MainFontColor, level.BackColor, 0.4);
				}
				else if (control is ShipStatusResource resource)
				{
					// 燃料・弾薬バーの背景色をテーマに合わせる
					ApplyStatusBarTheme(resource.BarFuel);
					ApplyStatusBarTheme(resource.BarAmmo);
				}
				else if (control is ShipStatusEquipment eq)
				{
					// 装備表示の文字色をテーマに合わせる
					eq.AircraftColorFull = GetForeColor(eq.BackColor);
					eq.AircraftColorDisabled = BlendColor(eq.AircraftColorFull, eq.BackColor, 0.4);
					eq.EquipmentLevelColor = Color.FromArgb(0x00, 0xCC, 0xCC); // 明るいシアン（視認性重視）
				}
			}
			// --- ラベル系: Label, ImageLabel ---
			else if (control is Label)
			{
				// ImageLabel は Label を継承しているため、ここでカバーされる
				control.ForeColor = GetForeColor(control.BackColor);
			}
			// --- 選択系: CheckBox, RadioButton, GroupBox ---
			else if (control is CheckBox || control is RadioButton || control is GroupBox)
			{
				control.ForeColor = GetForeColor(control.BackColor);
			}
			// --- 入力系: TextBox, ComboBox, ListBox ---
			else if (control is TextBox || control is ComboBox || control is ListBox)
			{
				control.BackColor = SubBackColor;
				control.ForeColor = ForeColor;
			}
			// --- ボタン ---
			else if (control is Button btn)
			{
				btn.FlatStyle = ButtonStyle;
				btn.BackColor = SubBackColor;
				btn.ForeColor = ForeColor;
				btn.FlatAppearance.BorderColor = GridColor;
			}
			// --- データグリッド ---
			else if (control is DataGridView dgv)
			{
				ApplyDataGridViewTheme(dgv);
			}
		}

		#endregion

		#region DataGridView テーマ適用

		/// <summary>
		/// DataGridView にテーマを適用します。
		/// ヘッダー、セル、交互行の配色をすべてテーマに合わせます。
		/// </summary>
		private static void ApplyDataGridViewTheme(DataGridView dgv)
		{
			// グリッド全体の基本色
			dgv.BackgroundColor = BackColor;
			dgv.ForeColor = ForeColor;
			dgv.GridColor = GridColor;

			// 標準のビジュアルスタイルを無効化（ヘッダーの色変更に必要）
			dgv.EnableHeadersVisualStyles = false;

			// デフォルトセルスタイル
			ApplyCellStyle(dgv.DefaultCellStyle);

			// 列ヘッダー
			ApplyCellStyle(dgv.ColumnHeadersDefaultCellStyle);
			dgv.ColumnHeadersDefaultCellStyle.BackColor = SubBackColor;

			// 行ヘッダー
			ApplyCellStyle(dgv.RowHeadersDefaultCellStyle);
			dgv.RowHeadersDefaultCellStyle.BackColor = SubBackColor;

			// 交互行の色（設定されている場合）
			if (dgv.AlternatingRowsDefaultCellStyle != null)
			{
				ApplyCellStyle(dgv.AlternatingRowsDefaultCellStyle);
			}

			// 各列の個別セルスタイル（任務画面などで個別設定されている対策）
			foreach (DataGridViewColumn column in dgv.Columns)
			{
				ApplyCellStyle(column.DefaultCellStyle);
			}
		}

		/// <summary>
		/// DataGridViewCellStyle にテーマを適用します。
		/// 背景色がシステム色の場合のみ上書きし、意図的に設定された色は維持します。
		/// </summary>
		private static void ApplyCellStyle(DataGridViewCellStyle style)
		{
			if (style == null) return;

			// 背景色がシステム色または未設定の場合のみテーマ色で上書き
			if (style.BackColor == SystemColors.Control ||
				style.BackColor == SystemColors.Window ||
				style.BackColor == Color.Empty)
			{
				style.BackColor = BackColor;
			}

			// 文字色は背景色に合わせて自動調整
			style.ForeColor = GetForeColor(style.BackColor);

			// 選択時の色
			style.SelectionBackColor = HighlightColor;
			style.SelectionForeColor = GetForeColor(HighlightColor);
		}

		#endregion

		#region ステータスバー（HPバー等）テーマ適用

		/// <summary>
		/// StatusBarModule (HPバーの描画エンジン) にテーマを適用します。
		/// バーの背景色をテーマのグリッド色に合わせます。
		/// </summary>
		private static void ApplyStatusBarTheme(StatusBarModule bar)
		{
			if (bar == null) return;
			bar.BarColorBackground = GridColor;
		}

		#endregion

		#region ユーティリティ

		/// <summary>
		/// 背景色に基づいて視認性の高い文字色（白または黒）を返します。
		/// ITU-R BT.601 の輝度計算式を使用しています。
		/// </summary>
		/// <param name="backColor">背景色</param>
		/// <returns>視認性の高い文字色</returns>
		private static Color GetForeColor(Color backColor)
		{
			// 背景が透明または未設定の場合はテーマの文字色をそのまま返す
			if (backColor == Color.Transparent || backColor == Color.Empty) return ForeColor;

			// 輝度計算: Y = 0.299R + 0.587G + 0.114B
			double brightness = (0.299 * backColor.R + 0.587 * backColor.G + 0.114 * backColor.B);

			// 暗い背景には白文字、明るい背景には黒文字
			return brightness < 128 ? Color.White : Color.Black;
		}

		/// <summary>
		/// 2つの色を指定した割合で混合します。
		/// サブテキスト色の生成などに使用されます。
		/// </summary>
		/// <param name="color">ベースの色</param>
		/// <param name="backColor">混合する色（背景色など）</param>
		/// <param name="weight">backColor の混合比率 (0.0〜1.0)</param>
		/// <returns>混合された色</returns>
		private static Color BlendColor(Color color, Color backColor, double weight)
		{
			return Color.FromArgb(
				(int)(color.R * (1 - weight) + backColor.R * weight),
				(int)(color.G * (1 - weight) + backColor.G * weight),
				(int)(color.B * (1 - weight) + backColor.B * weight));
		}

		#endregion
	}
}
