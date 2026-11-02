//============================================================================================
// system name    : ElectronicObserver
// program name   : RemoteController
// file name      : RemoteController.Geometry.cs
// summary        : ゲーム描画領域の実測ジオメトリ（単一供給源）・座標変換のフォールバック階層・フォーカス状態ログ
//----------------------------------------------------------------------------
// 実測を正とする理由 : 配信される映像は kcs2 canvas の実寸ビットマップだが、合成マウス入力は
//   Browser コントロール（view）座標系で渡す。両者の対応はページ側 CSS で決まる
//   （iframe の配置・canvas の内在寸法）ため、「コントロール内に中央寄せ 5:3 のゲーム領域が
//   ある」という仮定だけでは、スタイル未適用の窓でクリックを別位置へ着地させる。
//   着地点を保証できるのは実測のみなので、実測を 1 次的な供給源とする。
// フォールバック階層を残す理由 : 実測はメインフレームのプローブとエンジン報告が揃って
//   初めて得られる。未取得・陳腐化・妥当性検査不合格の窓で入力を止めないよう、
//   既知の状態（未適用期は恒等 / 適用期は 5:3 レターボックス）へ段階的に落とし、
//   どの段で変換したかを map 値として常に残す（適用期の計算は現行のまま据える）。
// スレッド安全性 : プローブ（CEF 完了継続）とエンジン報告（統計読み取りワーカー）が書き、
//   入力ワーカーが読む。部分更新された矩形を読ませないため、生成後に変更しない
//   スナップショットの参照を volatile フィールドへの単一の代入で交換する
//   （書込 = release、読取は 1 回の読み取りで不変コピーとして扱う = acquire）。
//   struct へ volatile を直接適用できない（CS0677）ため、値のコピーではなく参照の交換で表す。
// 1 秒サムリングの理由 : プローブも DOM 計測も JS 評価コストを伴う。実寸が変わる頻度は
//   低く、入力の着地精度に対して 1 秒の陳腐化は許容範囲のため、発火は 1 秒に 1 回かつ
//   実際に入力が使われた時だけとする（アイドル時に走らせない）。
//============================================================================================

using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using CefSharp;

namespace Browser
{
	public partial class RemoteController : IDisposable
	{
		//====================================================================================
		// 実測ジオメトリの状態（view 座標 = Browser コントロール座標）
		//====================================================================================

		/// <summary>
		/// view 座標系におけるゲーム描画領域の実測矩形。生成後は変更しない。
		/// </summary>
		private struct GameGeom
		{
			public double ViewX;
			public double ViewY;
			public double ViewW;
			public double ViewH;
			/// <summary>版番号。変換に使われたジオメトリをログで特定できるため、更新ごとに単調増加させる</summary>
			public long Seq;
			/// <summary>更新時刻（UTC ms）。鮮度判定に使う</summary>
			public long TsUtcMs;
			/// <summary>供給源: 0 = プローブ＋エンジン両方 / 1 = エンジンのみ / 2 = プローブのみ（iframe 矩形近似）</summary>
			public int Source;
		}

		/// <summary>
		/// 不変スナップショットのラッパー。seq とジオメトリを 1 つの参照で一体に可視化するため、
		/// 途中まで更新された値の組合せを観測することはない。
		/// </summary>
		private sealed class GameGeomSnapshot
		{
			public GameGeom G { get; }
			public GameGeomSnapshot(GameGeom g) { G = g; }
		}

		/// <summary>
		/// メインフレームプローブの報告（iframe 要素のメインフレーム内矩形とメインフレーム innerSize）。
		/// 生成後は変更しない。
		/// </summary>
		private sealed class ProbeGeomReport
		{
			public double Gx, Gy, Gw, Gh;
			public double Mw, Mh;
			public ProbeGeomReport(double gx, double gy, double gw, double gh, double mw, double mh)
			{
				Gx = gx; Gy = gy; Gw = gw; Gh = gh; Mw = mw; Mh = mh;
			}
		}

		/// <summary>
		/// キャプチャエンジン（kcs2 フレーム内）の報告（canvas 内在サイズ・canvas の
		/// kcs2 view 内矩形・kcs2 innerSize）。生成後は変更しない。
		/// </summary>
		private sealed class EngineGeomReport
		{
			public double Cw, Ch;
			public double Fx, Fy, Fw, Fh;
			public double Vw, Vh;
			public EngineGeomReport(double cw, double ch, double fx, double fy, double fw, double fh, double vw, double vh)
			{
				Cw = cw; Ch = ch; Fx = fx; Fy = fy; Fw = fw; Fh = fh; Vw = vw; Vh = vh;
			}
		}

		// 合成済みの実測矩形（座標変換が読む唯一の供給源）。null = 未取得
		private volatile GameGeomSnapshot _gameGeomRef;
		// 合成の入力となる直近報告（各報告は不変、参照の交換で更新する）
		private volatile ProbeGeomReport _probeGeomRef;
		private volatile EngineGeomReport _engineGeomRef;

		// long は volatile 適用不可のため Interlocked で扱う
		private long _geomSeq;              // 版番号（Interlocked.Increment）
		private long _lastProbeUtcTicks;    // プローブの最終試行時刻（1 秒サムリング）
		private int _probeRunning;          // single-flight（Interlocked.Exchange）
		private int _lastGeomSource = -1;   // 直近合成の供給源（変化した時だけ記録するため保持、変更時のみログ）

		// 実測矩形を使うための鮮度上限。エンジンは 1 秒サムリングで更新するため通常は数秒以内で、
		// この上限に達する値はページ遷移直後やエンジン不在期に古くなった値であり、
		// それで入力を誤った位置へ連れて行かないための境界（古い値の時はフォールバック階層へ落とす）
		private const long GeometryFreshMaxMs = 15000;
		// 妥当性検査の許容超過。DIP/CSS px 比を実測値（Browser.Width / innerWidth）で求めるための
		// 丸め・ズーム境界のズレを許し、コントロール寸法を明確に超える明らかな異常値だけを弾く
		private const double GeometrySizeTolerance = 1.05;
		// プローブの間隔（JS 評価コストのため 1 秒に 1 回、single-flight と併せて多重評価しない）
		private const long ProbeMinIntervalTicks = TimeSpan.TicksPerSecond;

		// プローブスクリプト（メインフレーム・1 評価で矩形と innerSize を一括取得）。
		// 候補は #game_frame（PageScript の既定構造）→ src に /kcs2/ を含む iframe → 最初の iframe。
		// 2 番目の候補は /kcs2/index.php を含む URL を漏らさないまま、index.php を含まない
		// 新しい SPA 構造の kcs2 iframe も捕まえることを意図した広い匹配であり、
		// キャプチャ源フレーム判定（IsKanColleFrame）と同一基準ではない。
		// 不一致（エンジン未注入）の iframe を掴んだ場合は canvas 内矩形が得られないため、
		// iframe 矩形近似（source=2）に落ちる。
		private const string GeometryProbeScript =
			"(function(){var el=document.querySelector('#game_frame');" +
			"if(!el){var ifrs=document.querySelectorAll('iframe');" +
			"for(var i=0;i<ifrs.length;i++){if((ifrs[i].src||'').indexOf('/kcs2/')!==-1){el=ifrs[i];break;}}" +
			"if(!el&&ifrs.length>0)el=ifrs[0];}" +
			"if(!el)return null;" +
			"var r=el.getBoundingClientRect();" +
			"return [r.left,r.top,r.width,r.height,window.innerWidth,window.innerHeight];})()";

		//====================================================================================
		// 処理ブロック名 : TryGetMapping
		// 概要           : 0.0〜1.0 の正規化座標を view 座標へ映射する。
		//                  実測が有効なら実測矩形、未取得・無効なら既知の状態へフォールバックし、
		//                  変換根拠（map）・ジオメトリ版（gseq）・変換に用いたビューポート寸法を
		//                  呼び出し側のログへそのまま渡す（値の取り違えと版の不一致を防ぐ）。
		//====================================================================================
		private bool TryGetMapping(double cursorX, double cursorY, out CoordTrace trace, out Point p)
		{
			p = Point.Empty;
			trace = CoordTrace.Skipped;

			// 変換に使うビューポート寸法はログの bw/bh と同一値にするため、ここで 1 回だけ読む
			double browserWidth = _parent.Browser.Width;
			double browserHeight = _parent.Browser.Height;

			// 0/極小ガード: 前提が崩れているため送らない（返値 false はこの場合のみ）
			if (browserWidth <= 0 || browserHeight <= 0)
			{
				trace = new CoordTrace
				{
					Map = "skipped",
					GSeq = 0,
					Bw = (int)browserWidth,
					Bh = (int)browserHeight
				};
				return false;
			}

			// ジオメトリはスナップショット参照を 1 回読むだけで決める（以降は不変コピー）
			var snap = _gameGeomRef;
			if (IsMeasuredUsable(snap, browserWidth, browserHeight))
			{
				// 実測矩形は DIP/CSS px 比の換算を経ているため、端の値がコントロール寸法を
				// わずかに超え得る。Cef に入力窓外の座標を渡さないための境界。
				int x = ClampToInt(snap.G.ViewX + cursorX * snap.G.ViewW, browserWidth);
				int y = ClampToInt(snap.G.ViewY + cursorY * snap.G.ViewH, browserHeight);
				p = new Point(x, y);
				trace = new CoordTrace
				{
					Bx = x,
					By = y,
					Map = "measured",
					GSeq = snap.G.Seq,
					Bw = (int)browserWidth,
					Bh = (int)browserHeight
				};
				return true;
			}

			// 実測を使えなかった窓は、次回の入力から実測を確保し直す（1 秒サムリング・single-flight）
			RefreshGameGeometryAsync();

			if (!_parent.IsStyleSheetApplied)
			{
				// スタイル未適用期: ゲーム領域を切り出す前提がないため、コントロール全体が
				// コンテンツ矩形という恒等写像で送る（レターボックス仮定より着地に近い）
				int x = (int)(cursorX * browserWidth);
				int y = (int)(cursorY * browserHeight);
				p = new Point(x, y);
				trace = new CoordTrace
				{
					Bx = x,
					By = y,
					Map = "identity_unapplied",
					GSeq = 0,
					Bw = (int)browserWidth,
					Bh = (int)browserHeight
				};
				return true;
			}

			// スタイル適用期: 艦これ標準 5:3（1200:720）が中央寄せで固定されている前提の
			// レターボックス変換（レイアウト固定機構が成立している窓で現行と同じ結果になる）
			double gameAspect = 5.0 / 3.0;
			double browserAspect = browserWidth / browserHeight;

			double gameWidth = browserWidth;
			double gameHeight = browserHeight;
			double offsetX = 0;
			double offsetY = 0;

			if (browserAspect > gameAspect)
			{
				// 横長（左右に黒い余白）
				gameWidth = browserHeight * gameAspect;
				offsetX = (browserWidth - gameWidth) / 2.0;
			}
			else
			{
				// 縦長（上下に黒い余白）
				gameHeight = browserWidth / gameAspect;
				offsetY = (browserHeight - gameHeight) / 2.0;
			}

			int ax = (int)(offsetX + cursorX * gameWidth);
			int ay = (int)(offsetY + cursorY * gameHeight);
			p = new Point(ax, ay);
			trace = new CoordTrace
			{
				Bx = ax,
				By = ay,
				Map = "assumed_5_3",
				GSeq = 0,
				Bw = (int)browserWidth,
				Bh = (int)browserHeight
			};
			return true;
		}

		/// <summary>
		/// 実測矩形を使ってよいかの検査。陳腐化値で入力を連れないための鮮度検査と、
		/// 合成式のガード（0 除算回避で 1 になる係数）が発火して退化した値や、
		/// コントロール寸法を明確に超える異常値を弾く妥当性検査を同じ条件で適用する
		/// （不合格のときはフォールバック階層へ落ち、map 値で区別できる）。
		/// </summary>
		private bool IsMeasuredUsable(GameGeomSnapshot snap, double browserWidth, double browserHeight)
		{
			if (snap == null)
				return false;

			long ageMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - snap.G.TsUtcMs;
			if (ageMs < 0 || ageMs > GeometryFreshMaxMs)
				return false;

			if (snap.G.ViewW <= 0 || snap.G.ViewH <= 0)
				return false;
			if (snap.G.ViewW > browserWidth * GeometrySizeTolerance)
				return false;
			if (snap.G.ViewH > browserHeight * GeometrySizeTolerance)
				return false;

			return true;
		}

		//====================================================================================
		// 処理ブロック名 : RefreshGameGeometryAsync
		// 概要           : メインフレームへプローブを 1 回評価し、エンジン報告と合成して
		//                  実測矩形を差し替える。座標変換が実測を使えなかった時だけを起点に
		//                  発火する（アイドル時に走らせない）。
		//====================================================================================
		private void RefreshGameGeometryAsync()
		{
			long nowTicks = DateTime.UtcNow.Ticks;
			if (nowTicks - Interlocked.Read(ref _lastProbeUtcTicks) < ProbeMinIntervalTicks)
				return;
			if (Interlocked.Exchange(ref _probeRunning, 1) != 0)
				return;
			Interlocked.Exchange(ref _lastProbeUtcTicks, nowTicks);

			try
			{
				var main = _parent.GetMainFrame();
				if (main == null)
				{
					// 無言で戻ると「実測不能な窓」と「プローブが空振りした窓」を区別できない
					Log(1, "[RemoteController] ジオメトリ刷新: メインフレームが未取得のため実測できません");
					Interlocked.Exchange(ref _probeRunning, 0);
					return;
				}

				// 入力経路で JS 評価を待たない（完了はプールスレッドで処理し、例外は必ずログへ残す）
				main.EvaluateScriptAsync(GeometryProbeScript).ContinueWith(t =>
				{
					try
					{
						HandleProbeResponse(t);
					}
					catch (Exception ex)
					{
						Log(1, $"[RemoteController] ジオメトリプローブ結果の処理に失敗: {ex.GetType().Name}: {ex.Message}");
					}
					finally
					{
						Interlocked.Exchange(ref _probeRunning, 0);
					}
				}, TaskScheduler.Default);
			}
			catch (Exception ex)
			{
				// 継続を組めなかった経路（フレーム破棄直後など）だけここで single-flight を解除する
				Interlocked.Exchange(ref _probeRunning, 0);
				Log(1, $"[RemoteController] ジオメトリプローブの開始に失敗: {ex.GetType().Name}: {ex.Message}");
			}
		}

		/// <summary>
		/// プローブ応答を検証して直近報告へ反映し、合成をやり直す。
		/// </summary>
		private void HandleProbeResponse(Task<JavascriptResponse> task)
		{
			if (task.IsFaulted)
			{
				Log(1, $"[RemoteController] ジオメトリプローブ評価失敗: {task.Exception?.GetBaseException().Message}");
				return;
			}

			var response = task.Result;
			if (response == null || !response.Success || response.Result == null)
			{
				// 候補要素なし（ゲーム iframe が存在しない窓）は実測不能の確定情報として残す
				Log(1, "[RemoteController] ジオメトリプローブ: 対象要素が見つからないか評価が失敗しました");
				return;
			}

			var list = response.Result as System.Collections.Generic.IList<object>;
			if (list == null || list.Count < 6)
			{
				Log(1, $"[RemoteController] ジオメトリプローブ応答の形が想定外です（要素数 {list?.Count ?? 0}）");
				return;
			}

			double gx, gy, gw, gh, mw, mh;
			if (!TryToDouble(list[0], out gx) || !TryToDouble(list[1], out gy) ||
				!TryToDouble(list[2], out gw) || !TryToDouble(list[3], out gh) ||
				!TryToDouble(list[4], out mw) || !TryToDouble(list[5], out mh))
			{
				Log(1, "[RemoteController] ジオメトリプローブ応答の数値変換に失敗しました");
				return;
			}

			_probeGeomRef = new ProbeGeomReport(gx, gy, gw, gh, mw, mh);
			ComposeGameGeometry();
		}

		/// <summary>
		/// キャプチャエンジン統計の追加要素から canvas 実測を受け取り、直近報告へ反映して合成し直す。
		/// </summary>
		private void OnCanvasGeometryReported(double canvasWidth, double canvasHeight,
			double frameX, double frameY, double frameW, double frameH, double viewWidth, double viewHeight)
		{
			_engineGeomRef = new EngineGeomReport(canvasWidth, canvasHeight, frameX, frameY, frameW, frameH, viewWidth, viewHeight);
			ComposeGameGeometry();
		}

		/// <summary>
		/// 直近のプローブ報告とエンジン報告から view 矩形を組み立て、スナップショットを参照交換する。
		/// 両フレームは同一ウィンドウの DPI のため CSS px は線形に換算できる前提で、
		/// iframe 内容のスケール（s）と view スケール（Browser.Width / innerWidth）を掛ける。
		/// スケールが計算できない場合は 1 を使う（退化した値は妥当性検査で弾かれ、
		/// measured として流れない）。
		/// </summary>
		private void ComposeGameGeometry()
		{
			var probe = _probeGeomRef;
			var engine = _engineGeomRef;
			if (probe == null && engine == null)
				return;

			double browserWidth = _parent.Browser != null ? _parent.Browser.Width : 0;

			double viewX, viewY, viewW, viewH;
			int source;

			if (probe != null && engine != null)
			{
				// iframe 要素のメインフレーム内矩形 + canvas の kcs2 view 内矩形 × 内容スケール
				double s = (probe.Gw > 0 && engine.Vw > 0) ? probe.Gw / engine.Vw : 1.0;
				double viewScale = (probe.Mw > 0) ? browserWidth / probe.Mw : 1.0;

				viewX = (probe.Gx + engine.Fx * s) * viewScale;
				viewY = (probe.Gy + engine.Fy * s) * viewScale;
				viewW = engine.Fw * s * viewScale;
				viewH = engine.Fh * s * viewScale;
				source = 0;
			}
			else if (probe != null)
			{
				// エンジン未注入（クロスオリジン等で canvas 内矩形が得られない）:
				// プローブが特定できた iframe 要素そのものをゲーム領域とみなす近似
				double viewScale = (probe.Mw > 0) ? browserWidth / probe.Mw : 1.0;

				viewX = probe.Gx * viewScale;
				viewY = probe.Gy * viewScale;
				viewW = probe.Gw * viewScale;
				viewH = probe.Gh * viewScale;
				source = 2;
			}
			else
			{
				// プローブ未取得（メインフレームがまだ無い等）でエンジン報告のみ:
				// iframe の配置が不明なため kcs2 view がコントロールに重なる近似
				double viewScale = (engine.Vw > 0 && browserWidth > 0) ? browserWidth / engine.Vw : 1.0;

				viewX = engine.Fx * viewScale;
				viewY = engine.Fy * viewScale;
				viewW = engine.Fw * viewScale;
				viewH = engine.Fh * viewScale;
				source = 1;
			}

			long seq = Interlocked.Increment(ref _geomSeq);
			_gameGeomRef = new GameGeomSnapshot(new GameGeom
			{
				ViewX = viewX,
				ViewY = viewY,
				ViewW = viewW,
				ViewH = viewH,
				Seq = seq,
				TsUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
				Source = source
			});

			// 供給源が切り替わった時だけ記録する（毎秒の合成では記録しない。
			// canvas 内矩形が取けていない（近似値）かを切り分ける手がかりはここに残す）
			if (source != _lastGeomSource)
			{
				_lastGeomSource = source;
				Log(3, $"[RemoteController] 実測ジオメトリ更新 (source={source}, seq={seq}, viewW={viewW:F1}, viewH={viewH:F1})");
			}
		}

		/// <summary>
		/// CefSharp が返す数値（double）・文字列の混在を invariant で double 化する。
		/// 既定カルチャ依存の小数点解釈を避ける（数値が壊れても例外にせず false で扱う）。
		/// </summary>
		private static bool TryToDouble(object value, out double result)
		{
			result = 0;
			if (value == null) return false;
			if (value is double d) { result = d; return true; }
			if (value is long l) { result = l; return true; }
			if (value is int i) { result = i; return true; }
			return double.TryParse(value.ToString(), System.Globalization.NumberStyles.Float,
				System.Globalization.CultureInfo.InvariantCulture, out result);
		}

		/// <summary>
		/// 変換結果をコントロール寸法の範囲へ収める（入力窓外の座標を Cef に渡さないための境界）。
		/// 切り捨ては既存の変換と同じ挙動に揃え、フォールバック階層間の比較で値がずれないようにする。
		/// </summary>
		private static int ClampToInt(double v, double max)
		{
			if (v <= 0) return 0;
			if (v >= max) return (int)max;
			return (int)v;
		}

		//====================================================================================
		// 処理ブロック名 : AppendFocusState
		// 概要           : フォーカス状態の観測行（kind=focus_state）を専用ログへ残す。
		//                  合成マウス入力にフォーカスゲートは存在しないため、
		//                  「届かない」原因が論理フォーカス側にあるかをこの行で切り分ける。
		//====================================================================================
		internal void AppendFocusState(string action, string context)
		{
			try
			{
				bool cefFocused = false;
				string focusedFrame = "none";
				var browser = _parent.Browser;
				if (browser != null)
				{
					// ChromiumWebBrowser.Focused は base.Focused（WinForms）→ IsHandleCreated →
					// Win32 のフォーカス判定の順で読む。入力ワーカーからの読取は
					// Control.CheckForIllegalCrossThreadCalls を有効にしない前提で許される
					// （有効化するとクロススレッド例外で入力経路自体を壊す）。
					cefFocused = browser.Focused;

					var ibrowser = browser.GetBrowser();
					var ff = ibrowser?.FocusedFrame;
					if (ff != null && !string.IsNullOrEmpty(ff.Url))
						focusedFrame = ff.Url;
				}

				// contains_focus（WinForms の Control.ContainsFocus）は UI スレッドでしか読まない。
				// 復元実行の記録は必ず UI スレッドディスパッチ内から呼ばれるため、この場合だけ読む。
				// click 系（ws/http）はフィールドを省略し、ワーカーからの WinForms プロパティ読取をしない。
				string containsFocusField = "";
				if (action == "focus_restored_setfocus")
					containsFocusField = ",\"contains_focus\":" + WriteJsonBool(_parent.ContainsFocus);

				RemoteDedicatedLog.Event.Append(
					$@"{{""ts_ms"":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},""ts_local"":""{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}"",""src"":""server"",""kind"":""focus_state"",""action"":""{action}"",""context"":""{context}"",""cef_focused"":{WriteJsonBool(cefFocused)},""focused_frame"":""{EscapeJsonString(focusedFrame, 512)}""{containsFocusField}}}");
			}
			catch (Exception ex)
			{
				// 入力経路の途中で呼ぶ観測処理なので、失敗を呼び出し側へ伝播させず記録だけ残す
				Log(3, $"[RemoteController] focus_state 記録失敗: {ex.GetType().Name}: {ex.Message}");
			}
		}
	}
}
