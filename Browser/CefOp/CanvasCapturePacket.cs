//============================================================================================
// system name    : ElectronicObserver
// program name   : CanvasCapturePacket
// file name      : CanvasCapturePacket.cs
// summary        : JavaScript経由でcanvasの描画バッファ（WebP/JPEG）を非同期取得するパケットクラス
//--------------------------------------------------------------------------------------------
// author         : myi
// creation date  : 2026/06/01
// version        : 1.00
//--------------------------------------------------------------------------------------------
#region "change history"
// change history : 2026/06/01 myi 新規作成
#endregion
// Copyright 2026 bEST ANSWER's Inc.
//============================================================================================

using System;
using System.IO;
using System.Threading.Tasks;

namespace Browser.CefOp
{
	/// <summary>
	/// JavaScript (CefSharp) から WebP 画面データと JPEG サムネイルデータを一括受信するためのパケットクラス。
	/// </summary>
	public class CanvasCapturePacket
	{
		// 識別用のユニークID
		public string ID { get; }

		// 受信したフル画面 WebP の Base64 データ
		public string FullWebp { get; private set; }

		// 受信したサムネイル JPEG の Base64 データ
		public string ThumbJpeg { get; private set; }

		// 非同期待機のためのタスクソース
		public TaskCompletionSource<CanvasCapturePacket> TaskSource { get; }

		/// <summary>
		/// コンストラクタ。ユニークなIDと非同期用タスクソースを初期化します。
		/// </summary>
		public CanvasCapturePacket() : this("cc_" + Guid.NewGuid().ToString("N")) { }

		public CanvasCapturePacket(string id)
		{
			ID = id;
			TaskSource = new TaskCompletionSource<CanvasCapturePacket>();
		}

		/// <summary>
		/// JavaScript 側から呼び出され、キャプチャ結果を受け取ってタスクを完了させます。
		/// </summary>
		/// <param name="fullWebp">フル解像度 WebP の Base64 文字列</param>
		/// <param name="thumbJpeg">サムネイル JPEG の Base64 文字列</param>
		public void Complete(string fullWebp, string thumbJpeg)
		{
			FullWebp = fullWebp;
			ThumbJpeg = thumbJpeg;
			TaskSource.SetResult(this);
		}

		/// <summary>
		/// 受信した Base64 文字列をバイト配列に変換します。
		/// </summary>
		/// <param name="dataUrl">Base64 データURL</param>
		/// <param name="prefix">期待されるMIMEプレフィックス（例: "data:image/webp"）</param>
		/// <returns>変換後のバイト配列。不正なデータの場合は null</returns>
		public static byte[] ConvertToBytes(string dataUrl, string prefix)
		{
			if (string.IsNullOrEmpty(dataUrl) || !dataUrl.StartsWith(prefix))
			{
				return null;
			}

			try
			{
				// "data:image/webp;base64," のヘッダ部分（カンマ以前）を除去してデコード
				int commaIdx = dataUrl.IndexOf(',');
				if (commaIdx == -1) return null;

				string base64Str = dataUrl.Substring(commaIdx + 1);
				return Convert.FromBase64String(base64Str);
			}
			catch (Exception)
			{
				return null;
			}
		}
	}
}
