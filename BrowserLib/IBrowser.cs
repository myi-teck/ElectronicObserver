using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceModel;
using System.Text;
using System.Threading.Tasks;

namespace BrowserLib
{
	/// <summary>
	/// FormBrowserのインターフェス
	/// WCFでプロセス間通信用
	/// </summary>
	[ServiceContract]
	public interface IBrowser
	{
		[OperationContract]
		void ConfigurationChanged(BrowserConfiguration conf);

		[OperationContract]
		void InitialAPIReceived();

		[OperationContract]
		Task SaveScreenShot();

		[OperationContract]
		void RefreshBrowser();

		[OperationContract]
		void UpdateHudData(string hudJson);

		[OperationContract]
		void ApplyZoom();

		[OperationContract]
		void Navigate(string url);

		/// <summary>
		/// プロキシをセット
		/// </summary>
		[OperationContract]
		void SetProxy(string proxy);

		[OperationContract]
		void ApplyStyleSheet();

		[OperationContract]
		void DestroyDMMreloadDialog();

		/// <summary>
		/// ダミー映像（検証用映像源）を開始する（永続値 ON 時の起動経路。通常トグルは HTTP/メニュー）。
		/// </summary>
		[OperationContract]
		void StartDummyVideo();

		[OperationContract]
		void CloseBrowser();

		[OperationContract]
		void SetIconResource(byte[] canvas);

	}
}
