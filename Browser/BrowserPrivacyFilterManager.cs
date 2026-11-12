//============================================================================================
// system name    : ElectronicObserver
// program name   : BrowserPrivacyFilterManager
// file name      : BrowserPrivacyFilterManager.cs
// summary        : プライバシーフィルタの管理・適用を行うマネージャークラス
//--------------------------------------------------------------------------------------------
// version        : 1.00
//============================================================================================

using CefSharp;
using System;
using System.Drawing;
using System.Windows.Forms;

namespace Browser
{
	internal class BrowserPrivacyFilterManager
	{
		private FormBrowser _formBrowser;
		private Timer _spotlightTimer;
		private Point _lastMousePos = new Point(-1, -1);

		public BrowserPrivacyFilterManager(FormBrowser formBrowser)
		{
			_formBrowser = formBrowser;

			_spotlightTimer = new Timer();
			_spotlightTimer.Interval = 33; // 約30fpsで座標更新
			_spotlightTimer.Tick += SpotlightTimer_Tick;
		}

		/// <summary>
		/// プライバシーフィルタおよびカバー機能を適用します。
		/// </summary>
		/// <param name="colorMode">0:なし 1:グレースケール 4:低コントラスト</param>
		/// <param name="appliesHoverCover">ホバー時のみ表示するカバー機能を有効にするか</param>
		public void ApplyFilter(int colorMode, bool appliesHoverCover)
		{
			if (!_formBrowser.IsBrowserInitialized)
				return;

			try
			{
				var gameFrame = _formBrowser.GetGameFrame() ?? _formBrowser.GetKanColleFrame();

				// ===== カバー機能(ホバー監視)のON/OFF切り替え =====
				if (appliesHoverCover)
				{
					_spotlightTimer.Start();
				}
				else
				{
					_spotlightTimer.Stop();
					var overlay = _formBrowser.PrivacyOverlayPanel;
					if (overlay != null)
					{
						overlay.Visible = false; // 無効時は確実にカバーを非表示に戻す
					}
				}

				// ===== ゲームフレーム用リセットスクリプト =====
				string resetGameFrameScript = @"
					document.documentElement.style.filter = 'none';
				";

				if (gameFrame != null)
				{
					gameFrame.EvaluateScriptAsync(resetGameFrameScript);
				}

				if (colorMode == 0) return; // なし

				string applyGameFrameScript = "";

				switch (colorMode)
				{
					case 1: // グレースケール
						applyGameFrameScript = "document.documentElement.style.filter = 'grayscale(100%)';";
						break;

					case 4: // 低コントラスト
						applyGameFrameScript = "document.documentElement.style.filter = 'grayscale(80%) brightness(30%) contrast(50%)';";
						break;
				}

				if (gameFrame != null && !string.IsNullOrEmpty(applyGameFrameScript))
				{
					gameFrame.EvaluateScriptAsync(applyGameFrameScript);
				}
			}
			catch (Exception ex)
			{
				_formBrowser.SendErrorReport("BrowserPrivacyFilterManager.ApplyFilter", ex.ToString());
			}
		}

		private void SpotlightTimer_Tick(object sender, EventArgs e)
		{
			if (!_formBrowser.IsBrowserInitialized)
				return;

			var control = _formBrowser.Browser as Control;
			if (control == null || !control.IsHandleCreated || control.IsDisposed)
				return;

			var screenPos = Cursor.Position;
			var clientPos = control.PointToClient(screenPos);

			var overlay = _formBrowser.PrivacyOverlayPanel;
			if (overlay == null)
				return;

			// ホバー（ブラウザのコントロール領域内にマウスが存在するか）判定
			bool isHovering = control.ClientRectangle.Contains(clientPos);

			// マウスが領域外なら目隠し（真っ暗）パネルを表示し、入っていれば非表示（ブラウザ画面を出す）
			if (isHovering && overlay.Visible)
			{
				overlay.Visible = false;
			}
			else if (!isHovering && !overlay.Visible)
			{
				// 最前面に持ってくる
				overlay.BringToFront();
				overlay.Visible = true;
			}
		}
	}
}
