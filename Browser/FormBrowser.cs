using Browser.CefOp;
using BrowserLib;
using CefSharp;
using CefSharp.WinForms;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.ServiceModel;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Browser
{
	/// <summary>
	/// ブラウザを表示するフォームです。
	/// </summary>
	/// <remarks>thx KanColleViewer!</remarks>
	[ServiceBehavior(InstanceContextMode = InstanceContextMode.Single/*, IncludeExceptionDetailInFaults = true*/)]
	public partial class FormBrowser : Form, BrowserLib.IBrowser
	{

		private readonly Size KanColleSize = new Size(1200, 720);
		private readonly string BrowserCachePath = "BrowserCache";
		protected string KanColleUrl => "https://play.games.dmm.com/game/kancolle";
		private string StyleClassId { get; } = Guid.NewGuid().ToString().Substring(0, 8);

		protected string PageScript => $@"
			(function applyPageCss() {{

				const STYLE_ID = '{StyleClassId}';

				function apply() {{
					try {{
						if (!document || !document.head) return false;

						let style = document.getElementById(STYLE_ID);
						if (!style) {{
							style = document.createElement('style');
							style.id = STYLE_ID;
							document.head.appendChild(style);
						}}

						style.textContent = `
							body {{
								margin: 0;
								padding: 0;
								min-width: 0;
								min-height: 0;
								overflow: hidden;
								background-color: black;
							}}

							#main-ntg {{
								position: static;
							}}

							#area-game {{
								margin-left: 0;
								margin-right: 0;
								padding: 0;
								width: 1200px;
								height: 720px;
								position: relative;
							}}

							.dmm-ntgnavi,
							.area-naviapp,
							#ntg-recommend,
							#foot,
							#foot+img {{
								display: none !important;
							}}

							#w, #main-ntg, #page {{
								margin: 0;
								padding: 0;
								width: 100%;
								height: 0;
								background: none !important;
							}}

							.gamesResetStyle,
							.gamesResetStyle * {{
								background: none !important;
							}}

							.gamesResetStyle > header,
							.gamesResetStyle > footer,
							.gamesResetStyle > aside {{
								display: none !important;
							}}

							#game_frame {{
								--game-frame-width: 1200px;
								--game-frame-height: 720px;
								position: fixed;
								top: 0;
								left: 0;
							}}
						`;
						return true;
					}} catch (e) {{
						return false;
					}}
				}}

				// 即時適用 + 自己修復
				if (!apply()) {{
					const timer = setInterval(() => {{
						if (apply()) clearInterval(timer);
					}}, 100);
				}}

			}})();
			";


		protected string FrameScript => $@"
			(function applyFrameCss() {{

				const STYLE_ID = '{StyleClassId}';

				function apply() {{
					try {{
						if (!document || !document.head) return false;

						let style = document.getElementById(STYLE_ID);
						if (!style) {{
							style = document.createElement('style');
							style.id = STYLE_ID;
							document.head.appendChild(style);
						}}

						style.textContent = `
							body {{
								visibility: hidden;
							}}

							#flashWrap {{
								position: fixed;
								left: 0;
								top: 0;
								width: 100% !important;
								height: 100% !important;
							}}

							#htmlWrap {{
								visibility: visible;
								width: 100% !important;
								height: 100% !important;
							}}
						`;
						return true;
					}} catch (e) {{
						return false;
					}}
				}}

				// 即時適用 + 自己修復
				if (!apply()) {{
					const timer = setInterval(() => {{
						if (apply()) clearInterval(timer);
					}}, 100);
				}}

			}})();
			";

		protected string RestoreScript => $@"
			(function restoreCss() {{

				function waitForHead() {{
					if (!document || !document.head) {{
						setTimeout(waitForHead, 50);
						return;
					}}

					try {{
						var node = document.getElementById('{StyleClassId}');
						if (node) {{
							node.remove();
						}}
					}} catch (e) {{
						console.error('RestoreScript: スタイル削除に失敗:', e);
					}}
				}}

				waitForHead();

			}})();
			";

		protected string DMMScript => @"
			(function disableDMMReloadDialog() {

				function waitForDMM() {
					try {
						// DMM.netgame が存在しない場合は待つ
						if (typeof DMM === 'undefined' ||
							!DMM.netgame ||
							typeof DMM.netgame.reloadDialog === 'undefined') {

							setTimeout(waitForDMM, 100);
							return;
						}

						// reloadDialog を無効化
						DMM.netgame.reloadDialog = function () {};

					} catch (e) {
						console.error('DMMScript: DMMリロードダイアログ無効化に失敗:', e);
					}
				}

				waitForDMM();

			})();
			";

		protected string OverrideReloadDialogScript => @"
			(function overrideConfirm() {

				function waitForWindow() {
					try {
						// window が未初期化なら待つ
						if (typeof window === 'undefined') {
							setTimeout(waitForWindow, 50);
							return;
						}

						// confirm が存在しない場合も待つ
						if (typeof window.confirm === 'undefined') {
							setTimeout(waitForWindow, 50);
							return;
						}

						// confirm を無効化
						Object.defineProperty(window, 'confirm', {
							configurable: true,
							writable: true,
							value: function() { return false; }
						});

					} catch (e) {
						console.error('OverrideReloadDialogScript: confirm() 無効化に失敗:', e);
					}
				}

				waitForWindow();

			})();
			";

		/// <summary>
		/// WebGLバッファクリア（真っ黒画面）問題を回避するため、ブラウザの描画フレーム（requestAnimationFrame）に完全同期して
		/// 軽量差分判定を行い、かつメインスレッドをブロックしない非同期toBlob + FileReaderによるバックグラウンドWebPキャッシュを行い、
		/// C#からの要求時にその最新キャッシュを非同期引き抜きするハイブリッドJavaScriptスクリプト。
		/// 変化がない場合はWebPエンコードを完全にスキップし、変化時のみ描画スレッドを占有せずバックグラウンドでWebP化を行います。
		/// 2026/06/02 myi 変更（同期toDataURLを完全廃止し、非同期toBlobによるバックグラウンドエンコードと多重起動防止を実装）
		/// </summary>
		internal string CaptureEngineScript => @"
			(function() {
				// 二重登録の防止
				if (window._kancolleCaptureEngine) return;
	
				// WebCodecs 用エンコードチャンクキュー（リングバッファ）
				var _encodedChunks = [];
				// 【task28 ver 3.9】 設計書 §5.1.1 対応: 最大エンコードチャンク数（30エントリ）
				// 目標FPS 30-60FPS において許容遅延を約0.5〜1秒に抑えるための値
				// （30エントリ × 1/30秒 ≒ 1秒: 30FPS時） / （30エントリ × 1/60秒 ≒ 0.5秒: 60FPS時）
				// 低帯域環境では遅延要因となるため、必要に応じて値を調整すること
				const MAX_ENCODED_CHUNKS = 30; // 【二次レビュー Minor 修正】 var→const 定数化（設計書 §5.1.1: 30エントリ）
				// 【二次レビュー Minor 指摘S3 修正】 stopEncoder の flush タイムアウト値を定数化
				const STOP_ENCODER_FLUSH_TIMEOUT_MS = 2000;
				// 【二次レビュー Minor 修正】 btoa() チャンク分割サイズの定数化
				// V8 の String.fromCharCode.apply の引数制限（スタックサイズ）の安全域として 8192 を選択
				// Chunks サイズを大きくするとスタックオーバーフロー、小さすぎると文字列連結の GC 負荷増大
				const BASE64_ENCODE_CHUNK_SIZE = 8192;

				// 差分検出（aHash）エンジン定数 — 調整可能（保守的閾値: 疑わしきは変化あり＝full 送信）
				const HASH_W = 96, HASH_H = 64;             // ハッシュ用ダウンスケールサイズ
				const HASH_CELLS_X = 12, HASH_CELLS_Y = 8;  // セルグリッド（1セル 8x8px）
				const HASH_CELL_DIST_THRESHOLD = 2;         // 任意セルの平均輝度距離 >=2 で変化ありと判定
	
				// 【task28 ver 3.9 + レビュー修正】 設計書 §5.1.2 対応: Promise ベースの排他ロック
				// 設計書定義通り _encodedChunksLock 構造体 + キュー方式 + タイムアウト 50ms に修正
				// 【Minor修正】 意図しない再代入を防止するため var→const 定数化
				const _encodedChunksLock = { locked: false, waiters: [] };
			
				// 【Minor修正】 意図しない再代入を防止するため var→const 定数化
				// 【Minor修正】 _acquireLock をより堅牢な形式にリファクタリング
				// - 内部関数の変数宣言を const/let に統一（var の関数スコープによる予期せぬ変数汚染を防止）
				// - タイムアウト定数化（50ms: 設計書 §5.1.2 ver 3.9 対応: 30FPSで約1.5フレーム分）
				const _acquireLockTimeoutMs = 50;
				const _acquireLock = async function() {
						return await new Promise(function(resolve, reject) {
							if (!_encodedChunksLock.locked) {
								_encodedChunksLock.locked = true;
								resolve();
							} else {
								// 50ms タイムアウト（設計書 §5.1.2 ver 3.9 対応: 30FPSで約1.5フレーム分）
								const thisWaiter = { resolve: resolve, reject: reject, timer: null };
								// 【レビュー修正】 setTimeout コールバックをアロー関数に変更（一次レビュー Minor 対応）
								// クロージャ変数 thisWaiter を安全に参照可能
								const timer = setTimeout(() => {
									const idx = _encodedChunksLock.waiters.indexOf(thisWaiter);
									if (idx !== -1) {
										_encodedChunksLock.waiters.splice(idx, 1);
									}
									reject(new Error('Lock acquisition timeout (' + _acquireLockTimeoutMs + 'ms)'));
								}, _acquireLockTimeoutMs);
								thisWaiter.timer = timer;
								_encodedChunksLock.waiters.push(thisWaiter);
							}
						});
					};
			
				// 【Minor修正】 意図しない再代入を防止するため var→const 定数化
				const _releaseLock = function() {
					if (_encodedChunksLock.waiters.length > 0) {
						const waiter = _encodedChunksLock.waiters.shift();
						if (waiter.timer) clearTimeout(waiter.timer);
						waiter.resolve(); // ロック所有権を直ちに解凍対象に引き継ぐ（locked は true のまま維持）
					} else {
						_encodedChunksLock.locked = false; // 待機者がいない場合のみ解放
					}
				};

				// Uint8Array → Base64 変換。btoa() は文字列全体に対して 1 回だけ呼び出す
				// （チャンク単位で btoa() すると 3 バイト境界のパディングが中間に残り不正な Base64 になる）。
				// String.fromCharCode.apply の V8 引数展開制限（65536）を避けるため chunkSize 単位で分割し、
				// 結合は join('') で O(n) とする（binary += 形式の O(n^2) を回避）
				function _bytesToBase64(bytes) {
					var chunkSize = BASE64_ENCODE_CHUNK_SIZE;
					var parts = [];
					for (var i = 0; i < bytes.length; i += chunkSize) {
						var end = Math.min(i + chunkSize, bytes.length);
						parts.push(String.fromCharCode.apply(null, bytes.subarray(i, end)));
					}
					return btoa(parts.join(''));
				}

				// 12x8 セルごとの平均輝度（average hash）を計算する。
				// getImageData 失敗時は null を返し、呼び出し側で「変化あり」（疑わしきは full）として扱う
				function _computeCellHash(ctx) {
					try {
						var img = ctx.getImageData(0, 0, HASH_W, HASH_H).data;
						var cells = new Uint8Array(HASH_CELLS_X * HASH_CELLS_Y);
						for (var cy = 0; cy < HASH_CELLS_Y; cy++) {
							for (var cx = 0; cx < HASH_CELLS_X; cx++) {
								var sum = 0, n = 0;
								for (var y = cy * 8; y < (cy + 1) * 8; y++) {
									var row = y * HASH_W;
									for (var x = cx * 8; x < (cx + 1) * 8; x++) {
										var i = (row + x) * 4;
										sum += (img[i] * 77 + img[i + 1] * 150 + img[i + 2] * 29) >> 8; // 輝度（ITU-R BT.601 の整数近似）
										n++;
									}
								}
								cells[cy * HASH_CELLS_X + cx] = (sum / n) | 0;
							}
						}
						return cells;
					} catch (e) {
						return null;
					}
				}

				// 前フレームのハッシュセルと比較し、任意セルの平均輝度距離が閾値以上なら true（保守判定）
				function _hashCellsChanged(prev, cur) {
					for (var i = 0; i < prev.length; i++) {
						var d = prev[i] - cur[i];
						if (d < 0) d = -d;
						if (d >= HASH_CELL_DIST_THRESHOLD) return true;
					}
					return false;
				}
	
				// 実測ジオメトリの取得: canvas の内在サイズ・kcs2 view 内の矩形・view 寸法。
				// 入力の着地先はこの実測値で決まるが、ページ側 CSS 次第で静的には導けない。
				// 計測失敗（canvas 未生成・ detached 等）は null を返す（C# 側は未取得として扱う）。
				function _readCanvasGeometry() {
					try {
						var c = document.querySelector('canvas');
						if (!c) return null;
						var r = c.getBoundingClientRect();
						return [c.width, c.height, r.left, r.top, r.width, r.height, window.innerWidth, window.innerHeight];
					} catch (e) {
						console.warn('[CaptureEngine] geom 計測失敗: ' + (e && e.message ? e.message : e));
						return null;
					}
				}

				// [task19_v1 Critical#5 修正] SPS/PPS 抽出・AVCDecoderConfigurationRecord (AVCC) 変換
				// 根本原因: Chromium 143 の H.264 エンコーダは onoutput の
				// metadata.decoderConfig.description を populate しない（常に null）。
				// W3C WebCodecs 仕様の decoderConfig 初期化アルゴリズムも codec/codedWidth/
				// codedHeight のみコピーし description を設定しないため、既存の
				// metadata.decoderConfig.description キャプチャ経路は機能しなかった。
				// 対策（候補B・確実）: 最初のキーフレームチャンクの byte 列から SPS/PPS NALU を
				// 抽出し、AVCDecoderConfigurationRecord (AVCC / ISO/IEC 14496-15 §5.3.3.1) へ
				// 変換して description として返す。annex-b（start code）と AVCC（length-prefixed）
				// の両形式に対応する。NALU type: 7 = SPS, 8 = PPS
				function _parseAnnexBNalus(bytes) {
					var n = bytes.length;
					var startPositions = [];
					var i = 0;
					while (i + 2 < n) {
						// [task19_v1 Round5 Minor-2 修正] 4-byte start code (00 00 00 01) を明示的に検出
						// 3-byte start code (00 00 01) の位置 (i+1) を記録し 4 バイトスキップすることで、
						// NALU 開始位置 (startPositions[k] + 3) が 01 バイトの直後 (i+4) となることを保証する
						if (bytes[i] === 0 && bytes[i + 1] === 0 && bytes[i + 2] === 0 && i + 3 < n && bytes[i + 3] === 1) {
							startPositions.push(i + 1);
							i += 4;
						} else if (bytes[i] === 0 && bytes[i + 1] === 0 && bytes[i + 2] === 1) {
							startPositions.push(i);
							i += 3;
						} else {
							i++;
						}
					}
					var nalus = [];
					for (var k = 0; k < startPositions.length; k++) {
						var nalStart = startPositions[k] + 3; // 00 00 01 の直後（4byte start code 00 00 00 01 も 00 00 01 として検出）
						if (nalStart >= n) continue;
						var nalEnd = (k + 1 < startPositions.length) ? startPositions[k + 1] : n;
						// trailing zero bytes を除去
						while (nalEnd > nalStart && bytes[nalEnd - 1] === 0) nalEnd--;
						if (nalEnd <= nalStart) continue;
						var nalUnit = bytes.subarray(nalStart, nalEnd);
						nalus.push({ type: nalUnit[0] & 0x1F, data: nalUnit });
					}
					return nalus;
				}
	
				function _parseAvccNalus(bytes) {
					var n = bytes.length;
					var nalus = [];
					var p = 0;
					while (p + 4 <= n) {
						// [task19_v1 Round5 Minor-1 修正] ビットシフト演算で 4 バイト大端長を復元
						// >>> 0 で unsigned 化し、符号拡張による負値を防止する
						var len = ((bytes[p] << 24) | (bytes[p + 1] << 16) | (bytes[p + 2] << 8) | bytes[p + 3]) >>> 0;
						if (len <= 0 || p + 4 + len > n) break;
						var nalUnit = bytes.subarray(p + 4, p + 4 + len);
						nalus.push({ type: nalUnit[0] & 0x1F, data: nalUnit });
						p += 4 + len;
					}
					return nalus;
				}
	
				function _buildAvcc(sps, pps) {
					// SPS NALU: [NAL header][profile_idc][constraint_flags][level_idc][...]
					var profileIdc = sps[1];
					var constraint = sps[2];
					var levelIdc = sps[3];
					var out = [];
					out.push(1);            // configurationVersion = 1
					out.push(profileIdc);   // AVCProfileIndication
					out.push(constraint);   // profile_compatibility
					out.push(levelIdc);     // AVCLevelIndication
					out.push(0xFF);         // reserved(6)=111111 + lengthSizeMinusOne(2)=11
					out.push(0xE0 | 1);     // numOfSequenceParameterSets = 1
					out.push((sps.length >> 8) & 0xFF);
					out.push(sps.length & 0xFF);
					for (var i = 0; i < sps.length; i++) out.push(sps[i]);
					out.push(1);            // numOfPictureParameterSets = 1
					out.push((pps.length >> 8) & 0xFF);
					out.push(pps.length & 0xFF);
					for (var j = 0; j < pps.length; j++) out.push(pps[j]);
					return new Uint8Array(out);
				}
	
				function _extractSpsPpsDescription(bytes) {
					var n = bytes.length;
					var isAnnexB = (n >= 4 && bytes[0] === 0 && bytes[1] === 0 && bytes[2] === 0 && bytes[3] === 1)
						|| (n >= 3 && bytes[0] === 0 && bytes[1] === 0 && bytes[2] === 1);
					var nalus = isAnnexB ? _parseAnnexBNalus(bytes) : _parseAvccNalus(bytes);
					var sps = null, pps = null;
					for (var i = 0; i < nalus.length; i++) {
						if (nalus[i].type === 7 && !sps) sps = nalus[i].data;
						else if (nalus[i].type === 8 && !pps) pps = nalus[i].data;
					}
					if (!sps || !pps) return null;
					return _buildAvcc(sps, pps);
				}
	
				var _requestKeyFrame = false;
	
				window._kancolleCaptureEngine = {
					webpQuality: 0.6,      // WebP圧縮品質
					lastWebp: null,        // バックグラウンド生成済みWebPキャッシュ
					lastWebpTs: 0,         // lastWebp 更新時刻（ms）— init 経路の新鮮性判定に使用
					encodeCount: 0,        // lastWebp 更新回数（内容更新本数）— サーバー側の二重レート計測用
					rafCount: 0,           // rAF 発火回数 — 内容新鮮度のボトルネック切り分け（toBlob 律速か canvas 再描画律速か）に使用
					rafStale: false,       // rAF ストール検知フラグ — 5 秒以上発火が無い時にウォッチドッグがセット（pull/peek 第4要素で C# へ伝達）
					lastRafTs: 0,          // 直近の rAF 発火時刻（performance.now）
					// 実測ジオメトリ [canvas内在幅, 内在高, canvas view内 left/top/width/height, innerWidth, innerHeight]。
					// 入力の着地先は canvas の実位置・実寸で決まるが、それはページ側 CSS で決まるため静的に導けない。
					// DOM 計測を配信毎に走らせないよう、別系統の 1 秒タイマで最新値だけを保持する（計装のみ・配信動作には影響しない）
					geom: null,
					isEncoding: false,     // 排他フラグ
					_dirtySincePull: false, // lastWebp が前回の pull 以降に更新されたか（WS 経路の stateful none 判定）

					// rAF ループの稼働ゲート（C# 側から setCaptureInterval / setActive で制御）。
					// rAF 自体は停止しない: Chromium では rAF を止めると復帰時に初回フレームの
					// 発火待ち遅延が生じるため、発火は維持し本体処理だけを間引く。
					//   active=false   … ループ本体を全面停止（コンシューマ不在時の省電力）
					//   intervalMs = 0 … 毎ティック処理（無制限）
					//   intervalMs > 0 … ハッシュ検出〜toBlob の最小間隔（ms）
					//   intervalMs < 0 … WebP エンコード無効（offCanvas の複製のみ維持＝WCS 専用運用）
					intervalMs: 0,
					active: true,
	
					// WebCodecs 状態管理（設計書 ver 3.9 §5.1.1 対応）
					videoEncoder: null,
					isReinitializing: false,
					encoderConfig: {
						codec: 'avc1.42001f',
						width: 1200,
						height: 720,
						bitrate: 800000,
						framerate: 30,
						avc: { format: 'annexb' }
					},
					encoderOutputCallback: null,
					encoderRunning: false,

					start: function() {
						const canvas = document.querySelector('canvas');
						if (!canvas) {
							setTimeout(() => this.start(), 150);
							return;
						}

						const offCanvas = document.createElement('canvas');
						const offCtx = offCanvas.getContext('2d');
						this.offCanvas = offCanvas;

						// 差分検出用ハッシュキャンバス: getImageData を毎ティック実行するため
						// willReadFrequently を指定し CPU バックエンド化で GPU 読み戻しストールを避ける（§6 R14）
						const hashCanvas = document.createElement('canvas');
						hashCanvas.width = HASH_W;
						hashCanvas.height = HASH_H;
						const hashCtx = hashCanvas.getContext('2d', { willReadFrequently: true });
						let prevHashCells = null;

						// WebGL バッファ保存 ＋ バックグラウンド WebP キャッシュ生成
						let lastWorkTs = 0;
						const loop = () => {
							this.rafCount++;
							this.lastRafTs = performance.now();
							if (this.rafStale) {
								this.rafStale = false;
								console.log('[CaptureEngine] rAF 発火が再開しました');
							}
							try {
								if (this.active && canvas.width > 0 && !this.isEncoding) {
									if (offCanvas.width !== canvas.width || offCanvas.height !== canvas.height) {
										offCanvas.width = canvas.width;
										offCanvas.height = canvas.height;
									}
									offCtx.drawImage(canvas, 0, 0);

									// 間隔スロットリング: offCanvas への複製は毎ティック実行し（WebGL バッファ保存機構と
									// WCS の VideoFrame 元画像を新鮮に保つため）、ハッシュ取得〜toBlob の重処理だけを
									// 間引く。intervalMs < 0 は WebP 側を完全停止（WCS 専用運用）
									const nowTs = performance.now();
									if (this.intervalMs < 0 ||
										(this.intervalMs > 0 && nowTs - lastWorkTs < this.intervalMs)) {
										requestAnimationFrame(loop);
										return;
									}
									lastWorkTs = nowTs;

									// 差分検出: 96x64 へダウンスケールし 12x8 セルの aHash 平均輝度距離を
									// 前フレームと比較。変化なしは toBlob エンコード自体をスキップ（lastWebp 未更新）。
									// offCanvas への drawImage は毎フレーム継続（WebGL バッファ保存機構を維持）
									hashCtx.drawImage(offCanvas, 0, 0, HASH_W, HASH_H);
									const hashCells = _computeCellHash(hashCtx);
									const contentChanged = hashCells === null || prevHashCells === null || _hashCellsChanged(prevHashCells, hashCells);
									prevHashCells = hashCells;

									if (contentChanged) {
										this.isEncoding = true;
										offCanvas.toBlob((blob) => {
											if (!blob) {
												this.isEncoding = false;
												return;
											}
											const reader = new FileReader();
											reader.onloadend = () => {
												if (reader.result) {
													this.lastWebp = reader.result;
													this.lastWebpTs = Date.now();
													this.encodeCount++;
													// ダーティフラグは lastWebp 更新完了後にセット:
													// 検出時点でセットすると encoding 中に pull がフラグを消費し、
													// エンコード中のフレームが次回変化まで配信されない
													this._dirtySincePull = true;
												}
												this.isEncoding = false;
											};
											reader.onerror = () => {
												this.isEncoding = false;
											};
											reader.readAsDataURL(blob);
										}, 'image/webp', this.webpQuality);
									}
								}
							} catch (e) {
								this.isEncoding = false;
							}
							requestAnimationFrame(loop);
						};
						requestAnimationFrame(loop);

						// rAF ストール検知（ヘッドレス/RDP 切断時の描画凍結対策）:
						// setInterval は rAF とは別系統のタイマーで発火するため、rAF が止まっても動作する。
						// auto-FPS 降格はループ本体の間引きのみで rAF 発火は維持されるため誤検知しない。
						// 閾値 5 秒は通常運用（60FPS 相当）に対する十分大きな余裕
						if (!this._rafWatchdogTimer) {
							this._rafWatchdogTimer = setInterval(() => {
								// 実測ジオメトリの 1 秒サムリング。rAF が止まっている窓でも入力の着地計算には
								// 必要なので、rAF 稼働判定より先に測る（配信・描画動作には影響しない計装）
								this.geom = _readCanvasGeometry();
								if (!this.lastRafTs) return;
								const gap = performance.now() - this.lastRafTs;
								if (gap >= 5000 && !this.rafStale) {
									this.rafStale = true;
									console.warn('[CaptureEngine] rAF 停止検知: 最後の発火から ' + Math.round(gap / 1000) + '秒');
								}
							}, 1000);
						}
					},

					// C# 側（ExecuteJavaScriptAsync）からの稼働制御。rAF の発火は止めずに本体処理だけを間引く
					setCaptureInterval: function(ms) {
						const v = Number(ms);
						this.intervalMs = isFinite(v) ? Math.floor(v) : 0;
					},
					setActive: function(active) {
						this.active = !!active;
					},

					// C# から EvaluateScriptAsync で呼び出され、事前に完成している WebP キャッシュを即座(0.5ms)に渡す
					// 差分検出（aHash）連動: ダーティ（前回の pull 以降に lastWebp が更新された）場合のみ full を返し
					// フラグを消費する。非ダーティかつ force=false は ['none',''] を返す（中央ループは配信スキップ、HTTP は 204）。
					// force=true（init 経路）は常に full を返す新鮮性契約を維持し、
					// lastWebp が 3 秒以上陳腐化している場合のみ toDataURL で新鮮画像を優先取得する。
					// toDataURL は同期実行で重い（N100 で 3-7/s）ため、発火は init 経路に限定する
					// 第3要素 encodeCount: lastWebp 更新回数＝コンテンツ版数（HTTP の cver 判定に使用）
					// 第4要素 rafStale: rAF ストール検知フラグ（C# 側で display_state render_stall 判定に使用）
					pull: function(force) {
						if (this._dirtySincePull && this.lastWebp) {
							this._dirtySincePull = false;
							return ['full', this.lastWebp, this.encodeCount, this.rafStale];
						}
						if (force !== true) {
							return ['none', '', this.encodeCount, this.rafStale];
						}
						// force=true（init 経路）: 新鮮でなければ toDataURL フォールバックで新鮮画像を優先
						var stale = this.lastWebpTs > 0 && (Date.now() - this.lastWebpTs) >= 3000;
						if (this.lastWebp && !stale) {
							return ['full', this.lastWebp, this.encodeCount, this.rafStale];
						}
						// [🔍 plan02 1-4] toDataURL フォールバック検証
						// toBlobでデコード失敗した際にtoDataURLを試行し、プレフィックスを検証
						const canvas = document.querySelector('canvas');
						if (canvas) {
							try {
								const dataUrl = canvas.toDataURL('image/webp', this.webpQuality);
								// [検証] プレフィックスが 'data:image/webp;base64,' であることを確認
								var expectedPrefix = 'data:image/webp;base64,';
								if (dataUrl.startsWith(expectedPrefix)) {
									console.log('[plan02-1-4] toDataURLフォールバック成功: プレフィックスOK (data:image/webp;base64,)');
								} else {
									console.warn('[plan02-1-4] toDataURLフォールバック: 予期しないプレフィックス ' + dataUrl.substring(0, 40) + '...');
								}
								return ['full', dataUrl, this.encodeCount, this.rafStale];
							} catch (e) {
								console.error('[plan02-1-4] toDataURLフォールバック失敗:', e.message);
							}
						}
						// 新鮮取得に失敗しても古いキャッシュがあれば返す（init 経路でも 'none' より描画を優先）
						if (this.lastWebp) {
							return ['full', this.lastWebp, this.encodeCount, this.rafStale];
						}
						return ['none', '', this.encodeCount, this.rafStale];
					},

					// HTTP 配信専用の読み取り専用引き抜き: ダーティフラグを消費せず toDataURL も発火しない。
					// cver < 現在版数のクライアントへ full を返す用途（複数 HTTP クライアントの無状態判定）。
					// 初回フレーム生成前は ['none','']（C# 側は 503 相当として扱う）
					peek: function() {
						if (this.lastWebp) {
							return ['full', this.lastWebp, this.encodeCount, this.rafStale];
						}
						return ['none', '', this.encodeCount, this.rafStale];
					},

					//================================================================================
					// WebCodecs エンコーダ機能（task28 Phase 2）
					//--------------------------------------------------------------------------------

					// initEncoder(): VideoEncoder の構築・configure()・encoderOutputCallback 設定
					// 【レビュー修正】 設計書 ver 3.9 §5.1.1 対応: ロック保護付きの encoderOutputCallback 退避・キュークリア
					initEncoder: async function(config) {
						// 再入防止（設計書 S7 対応）
						if (this.isReinitializing) {
							return { success: false, error: 'Already reinitializing' };
						}
						this.isReinitializing = true;
						this.encoderRunning = false;

						if (!('VideoEncoder' in window)) {
							this.isReinitializing = false;
							return { success: false, error: 'VideoEncoder not supported' };
						}

						// Secure Context 確認
						if (window.isSecureContext === false) {
							this.isReinitializing = false;
							return { success: false, error: 'Not a Secure Context' };
						}

						// 【レビュー修正】 設計書 ver 3.9 §5.1.1 対応: 旧 encoderOutputCallback 退避（ロック取得前）
						var encoderOutputCallbackBeforeLock = this.encoderOutputCallback;

						try {
							// 【レビュー修正】 ロック取得 → encoderOutputCallback null 化 → キュークリアの順序
							await _acquireLock();
							try {
								// ロック取得成功後に encoderOutputCallback を null 化
								this.encoderOutputCallback = null;
								// キュークリア（ロック保護下で安全）
								// 【Major修正】 設計書 §5.1.1 対応: splice方式で配列参照を維持
								_encodedChunks.splice(0, _encodedChunks.length);
							} finally {
								_releaseLock();
							}

							if (this.videoEncoder) {
								await this.stopEncoder();
								this.videoEncoder = null;
							}

							if (!config.avc) {
								config.avc = { format: 'annexb' };
							}

							// コーデックサポート確認
							var supported = await VideoEncoder.isConfigSupported({
								codec: config.codec,
								width: config.width,
								height: config.height,
								bitrate: config.bitrate,
								avc: config.avc
							});

							if (!supported.supported) {
								this.isReinitializing = false;
								// 【Minor修正】 エラーメッセージを具体化: codec/width/height/bitrate/reason を含めて出力
								// supported.reason には 'unsupported' / 'not-efficient' 等の詳細理由が含まれる
								var errorMsg = 'Config unsupported: codec=' + config.codec
									+ ', width=' + config.width
									+ ', height=' + config.height
									+ ', bitrate=' + config.bitrate
									+ ', reason=' + (supported.reason || 'unknown');
								return { success: false, error: errorMsg };
							}

							// VideoEncoder 構築
							this.videoEncoder = new VideoEncoder({
								output: (chunk, metadata) => {
									if (this.encoderOutputCallback) {
										this.encoderOutputCallback(chunk, metadata);
									}
								},
								error: (e) => {
									console.error('[CaptureEngine] VideoEncoder error:', e);
									this.encoderRunning = false;
								}
							});

							// configure() 実行
							this.videoEncoder.configure(config);
							this.encoderConfig = config;
							// [task19_v1 Critical#5 修正] SPS/PPS 抽出フラグをリセット（再初期化時に新しい SPS/PPS を再抽出）
							this._spsPpsExtracted = false;

							// 【レビュー修正】 設計書 ver 3.9 §5.1.1 対応: encoderOutputCallback 内のロック保護
							var self = this;
							this.encoderOutputCallback = async function(chunk, metadata) {
								var view = new Uint8Array(chunk.byteLength);
								chunk.copyTo(view);

								// [task19_v1 Critical#5 修正] SPS/PPS (description) キャプチャ（候補B・確実）
								// 根本原因: Chromium 143 の H.264 エンコーダは onoutput の
								// metadata.decoderConfig.description を populate しない（常に null）。
								// W3C WebCodecs 仕様の decoderConfig 初期化アルゴリズムも codec/codedWidth/
								// codedHeight のみコピーし description を設定しないため、既存の
								// metadata.decoderConfig.description キャプチャ経路は機能しなかった。
								// 対策: 最初のキーフレームチャンクの byte 列から SPS/PPS NALU を抽出し、
								// AVCDecoderConfigurationRecord (AVCC / ISO/IEC 14496-15 §5.3.3.1) へ変換する。
								// annex-b（start code）と AVCC（length-prefixed）の両形式に対応。
								// 抽出は 1 回だけ（_spsPpsExtracted フラグで管理）。initEncoder() でフラグをリセットするため、
								// reinit（再初期化）後は新しい SPS/PPS を再抽出する。
								var description = null;
								if (chunk.type === 'key') {
									// ① 仕様準拠経路: 将来 Chromium が decoderConfig.description を populate した場合に備え
									//    最優先で利用する（現行 Chromium 143 では常に null であるため ② にフォールバック）
									if (metadata && metadata.decoderConfig && metadata.decoderConfig.description) {
										// [task19 レビュー指摘 M4/Minor] バッファコピー安全性:
										// Uint8Array.from() は常に独立した新 ArrayBuffer を生成するため、参照共有を排除する
										description = Uint8Array.from(metadata.decoderConfig.description);
										this._spsPpsExtracted = true;
									}
									// ② [task19_v1 Critical#5] チャンク byte 列から SPS/PPS を抽出（候補B・確実）
									//    SPS/PPS は AVC セッション内で不変なため、初回キーフレームのみ抽出（1 回だけ）
									else if (!this._spsPpsExtracted) {
										var extracted = _extractSpsPpsDescription(view);
										if (extracted) {
											description = extracted;
											this._spsPpsExtracted = true;
											console.log('[CaptureEngine] SPS/PPS extracted from first keyframe, AVCC length=' + extracted.length);
										} else {
											console.warn('[CaptureEngine] SPS/PPS extraction failed from keyframe (no SPS/PPS NALU found)');
										}
									}
								}

								try {
									await _acquireLock();
									try {
										_encodedChunks.push({
											data: view.buffer.slice(view.byteOffset, view.byteOffset + view.byteLength),
											isKeyFrame: chunk.type === 'key',
											timestamp: chunk.timestamp,
											description: description,
											enqueueTime: performance.now()
										});
										// リングバッファ上限（MAX_ENCODED_CHUNKS エントリ）
										if (_encodedChunks.length > MAX_ENCODED_CHUNKS) {
											_encodedChunks.shift();
										}
									} finally {
										_releaseLock();
									}
								} catch (e) {
									// ロック取得タイムアウト時はそのフレームをドロップ
									console.warn('[CaptureEngine] encoderOutputCallback lock timeout, dropping frame:', e.message);
								}
							};

							this.isReinitializing = false;
							this.encoderRunning = true;
							return { success: true, codec: config.codec };

						} catch (e) {
							// 【レビュー修正】 タイムアウト時は旧 encoderOutputCallback を復元
							this.encoderOutputCallback = encoderOutputCallbackBeforeLock;
							this.isReinitializing = false;
							this.videoEncoder = null;
							this.encoderRunning = false;
							return { success: false, error: 'configure failed: ' + e.message };
						}
					},

					// encodeFrame(): VideoFrame をエンコーダに投入し、close() で解放
					// 【Critical修正】 try-finally で frame.close() を確実に実行し、VRAMリークを防止
					// 【二次レビュー Minor 指摘S2 修正】 _encodeErrorCount のスレッド安全性確保
					// 将来的な並列化に備え、_encodeErrorCount の読み書きは _acquireLock() / _releaseLock() で保護すること
					// 現状は単一スレッド（WebCodecsキャプチャループ内逐次実行）であり競合は発生しないが、
					// 将来マルチスレッド化された場合はこの関数を async 化し、await _acquireLock() で保護すること
					encodeFrame: function(canvas, isKeyFrame) {
							if (!this.encoderRunning || !this.videoEncoder) {
								return { success: false, error: 'Encoder not running' };
							}
	
							// state チェック
							if (this.videoEncoder.state === 'closed') {
								this.encoderRunning = false;
								return { action: 'reinit' };
							}
							if (this.videoEncoder.state === 'unconfigured') {
								this.encoderRunning = false;
								return { action: 'reinit' };
							}
	
							// 【レビュー修正】 VideoFrame 生成エラーカウント（二次レビュー Minor 対応）
							// 連続失敗時に自動再初期化をトリガー（閾値: 10回）
							// 【二次レビュー Minor 指摘S3 修正】
							// 将来並列化時は Atomics または _acquireLock() / _releaseLock() で保護が必要
							// 現状は単一スレッド実行であり、JavaScript の非同期コンテキストで競合しない
							if (!this._encodeErrorCount) this._encodeErrorCount = 0;
	
							var frame = null;
							try {
								var fps = (this.encoderConfig && this.encoderConfig.framerate) || 30;
								if (!this._frameTimestampCounter) this._frameTimestampCounter = 0;
								this._frameTimestampCounter += Math.floor(1000000 / fps);
								var frameTimestamp = this._frameTimestampCounter;
								var sourceCanvas = this.offCanvas || canvas;
								frame = new VideoFrame(sourceCanvas, {
									alpha: 'discard',
									duration: 1000000 / this.encoderConfig.framerate,
									timestamp: frameTimestamp
								});
	
								this.videoEncoder.encode(frame, {
									keyFrame: isKeyFrame || false
								});
	
								// 成功時はエラーカウントをリセット
								this._encodeErrorCount = 0;
								return { type: 'encoded' };
							} catch (e) {
								this._encodeErrorCount++;
								console.warn('[CaptureEngine] VideoFrame encode failed: ' + e.message + ' (count=' + this._encodeErrorCount + ')');
								// 【レビュー修正】 連続エラー閾値超過時は自動再初期化をトリガー
								if (this._encodeErrorCount >= 10) {
									this._encodeErrorCount = 0;
									console.warn('[CaptureEngine] encode error count exceeded, triggering reinit');
									return { action: 'reinit' };
								}
								return { success: false, error: 'encode failed: ' + e.message };
							} finally {
								// 【Critical修正】 エラー経路を含め frame.close() を確実に実行（VRAMリーク防止）
								if (frame) {
									frame.close();
								}
							}
						},

					// pull_encoded(): _encodedChunks キューから EncodedVideoChunk を取り出し、Base64 として返却
					// 【レビュー修正】 設計書 ver 3.9 §5.1.2 対応: _encodedChunks アクセスをロックで保護
					// 【レビュー修正】 isReinitializing チェックをロック取得前に移動（設計書 §5.1.2 定義順）
					pull_encoded: async function() {
						// isReinitializing チェック（ロック取得前: ロック中に reinitializing 変更時に無駄な処理を防ぐ）
						if (this.isReinitializing) {
							return ['none', null];
						}

						await _acquireLock();
						try {

							if (_encodedChunks.length === 0) {
								return ['none', null];
							}

							var entry = _encodedChunks.shift();
							if (!entry || !entry.data) {
								return ['none', null];
							}

							// 【Critical修正】 Base64 チャンク分割バグ修正: btoa() を全体に対して1回だけ呼び出す
							// 旧実装ではチャンク単位でbtoa()を呼び出し結合していたが、
							// 3バイト境界でパディングが挿入されるため中間パディングを含む不正なBase64が生成される
							// 修正案: チャンク単位で文字列を構築し、結合後の文字列全体に対してbtoa()を1回だけ呼び出す
							//
							// 【Critical修正】 String.fromCharCode.apply のパフォーマンス改善:
							// V8 の引数展開制限（65536 引数）と文字列連結の O(n²) コストを回避。
							// チャンク単位で String.fromCharCode.apply を呼び出し、Array.join('') で O(n) 連結。
							// binary += String.fromCharCode.apply(...) の文字列連結が O(n²) となる問題を解消。
							//
							// 【二次レビュー Minor 修正】 btoa() 実行負荷について:
							// 毎フレーム btoa() を呼び出すことによる GC 負荷の懸念があるが、
							// チャンク単位(8192バイト)で文字列を構築し、btoa() は1回/フレームで実行するため、
							// 中間の一時文字列は 1フレーム × 1回 のみ。エンコードデータ量 ≒ 100KB の場合、
							// btoa() は約 133KB の文字列を処理する。
							// 再利用可能なバッファによる最適化は機能の正し性を損なうリスクがあるため、
							// 現行の簡潔な実装を維持する（V8のbtoaは内部で高速なネイティブ実装を使用）。
							var base64 = _bytesToBase64(new Uint8Array(entry.data));
	
							// [task19 P2] SPS/PPS (description) を第4要素として返却（設計書 §4.2.2 ステップ1-2）
							// description がある場合のみ Base64 文字列、なければ null
							// C# 側が Convert.ToBase64String() 相当で復号し、Type=0x02 フレームでクライアントへ送信する
							var descriptionBase64 = null;
							if (entry.description && entry.description.length > 0) {
								descriptionBase64 = _bytesToBase64(entry.description);
							}
	
							return ['encoded', base64, entry.isKeyFrame ? '1' : '0', descriptionBase64];
						} catch (e) {
							console.error('[CaptureEngine] pull_encoded error:', e);
							return ['none', null];
						} finally {
							_releaseLock();
						}
					},

					// encodeAndPull(): encodeFrame() 投入と既生成チャンクの全ドレインを 1 回のスクリプト評価で行う。
					// 従来は encodeFrame() と pull_encoded() の 2 往復（CefSharp IPC × 2）が必要で、
					// 低 FPS 運用でもフレーム毎の往復オーバーヘッドが固定費として乗っていたため統合した。
					// 返却値:
					//   { action:'reinit' } / { success:false, error } … encodeFrame() の失敗をそのまま伝播（C# の再初期化分岐を維持）
					//   ['none']                                       … エンコードは成功したが回収可能なチャンクはまだ無い
					//   ['encoded', [[base64, isKey, descBase64], …]]  … 回収チャンク一覧
					// エンコーダ出力は非同期（output コールバック）のため、投入時に回収済みのチャンクが
					// 複数残ることがある。全件を返し、欠番なく配信する（リング上限 MAX_ENCODED_CHUNKS で有界）
					encodeAndPull: async function(isKeyFrame) {
						var canvas = document.querySelector('canvas');
						if (!canvas) return { success: false, error: 'Canvas not found' };

						var enc = this.encodeFrame(canvas, isKeyFrame);
						if (enc && (enc.action === 'reinit' || enc.success === false)) {
							return enc;
						}

						// 再初期化中はチャンクキューが破棄され得るため回収しない（pull_encoded() と同じ契約）
						if (this.isReinitializing) return ['none'];

						await _acquireLock();
						try {
							if (_encodedChunks.length === 0) return ['none'];

							var out = [];
							while (_encodedChunks.length > 0) {
								var entry = _encodedChunks.shift();
								if (!entry || !entry.data) continue;

								var descBase64 = null;
								if (entry.description && entry.description.length > 0) {
									descBase64 = _bytesToBase64(entry.description);
								}
								out.push([_bytesToBase64(new Uint8Array(entry.data)), entry.isKeyFrame ? '1' : '0', descBase64]);
							}

							if (out.length === 0) return ['none'];
							return ['encoded', out];
						} catch (e) {
							console.error('[CaptureEngine] encodeAndPull error:', e);
							return ['none'];
						} finally {
							_releaseLock();
						}
					},

					// stopEncoder(): encoder.flush() の待機および encoder.close() の実行
					// 【レビュー修正】 Promise.race による flush タイムアウト処理（デッドロック防止）
					stopEncoder: async function() {
						if (!this.videoEncoder) {
							return;
						}

						try {
							// 【レビュー修正】 Promise.race で flush() タイムアウト処理
							// flush() 正常完了 vs 2秒タイムアウトの競争
							var flushPromise = this.videoEncoder.flush();
							// 【二次レビュー Minor 指摘S3 修正】 setTimeout コールバックをアロー関数化 + 定数化
							var timeoutPromise = new Promise(resolve => {
								setTimeout(() => resolve('timeout'), STOP_ENCODER_FLUSH_TIMEOUT_MS);
							});

							var result = await Promise.race([flushPromise, timeoutPromise]);

							if (result === 'timeout') {
								console.warn('[CaptureEngine] flush() timeout, forcing reset()');
								// タイムアウト時は reset() で強制リセット
								if (this.videoEncoder && this.videoEncoder.state !== 'closed') {
									this.videoEncoder.reset();
								}
							}

							// close() 実行
							if (this.videoEncoder && this.videoEncoder.state !== 'closed') {
								this.videoEncoder.close();
							}
						} catch (e) {
								console.error('[CaptureEngine] stopEncoder error:', e);
								if (this.videoEncoder && this.videoEncoder.state !== 'closed') {
									try { this.videoEncoder.reset(); } catch (e2) {
										// 【レビュー修正】 reset() 失敗時の console.error 出力（二次レビュー Minor 対応）
										console.error('[CaptureEngine] reset() failed after stopEncoder error:', e2);
									}
								}
						} finally {
							this.videoEncoder = null;
							this.encoderRunning = false;
						}
					}
				};

				window._kancolleCaptureEngine.start();
			})();
			";

		private bool RestoreStyleSheet = false;

		// FormBrowserHostの通信サーバ
		private string ServerUri;

		// FormBrowserの通信サーバ
		private PipeCommunicator<BrowserLib.IBrowserHost> BrowserHost;

		// RemoteController 側（同一アセンブリの別 partial）からも焦点復元の判定キーを読むため internal にする
		internal BrowserLib.BrowserConfiguration Configuration;

		// 親プロセスが生きているか定期的に確認するためのタイマー
		private Timer HeartbeatTimer = new Timer();
		private IntPtr HostWindow;


		internal ChromiumWebBrowser Browser = null;

		private volatile RemoteController _remoteController;

		// 2026/07/26 myi 新規作成（RemoteController 初期化前のHUDデータバッファ）
		/// <summary>
		/// _remoteController が null の間に到着したHUDデータをバッファリングします。
		/// _remoteController が初期化された後にフラッシュされます。
		/// </summary>
		private string _bufferedHudJson = null;

		// 2026/07/26 myi 新規作成（HUDデータバッファの同期用ロック）
		/// <summary>
		/// _bufferedHudJson へのアクセスを同期するためのロックオブジェクトです。
		/// </summary>
		private readonly object _hudBufferLock = new object();

		private string ProxySettings = null;

		// [task19 不具合3修正] APIObserver 下流プロキシの既定値
		// （SetProxy がホストから届く前・ProxySettings が null/未パースの場合のフォールバック）
		// Program.cs が Application.Run 以前に CefSharpSettings.Proxy を既定値で早期設定するのにも使用
		public const string DefaultApiObserverHost = "127.0.0.1";
		public const string DefaultApiObserverPort = "40620";
	
		// InitializeBrowser 呼び出しの排他ロック用オブジェクト
		private readonly object _initBrowserLock = new object();


		// UI スレッド・CEF イベントスレッド・BeginInvoke 継続の複数スレッドから読み書きされる。
		// 入力座標変換（ワーカースレッド）が読むため volatile とする。偶発的に古い値を読む被害は、
		// 未適用期は恒等・適用期は 5:3 固定（≡ 恒等）というフォールバック階層の安全側で吸収されるため、
		// 追加のロックは取らない。
		private volatile bool _styleSheetApplied;

		// ウィンドウ最小化・非表示でコントロールが極小化されたまま残ったときに戻す「直前の有効サイズ」のコピー。
		// 書き込みは RememberBrowserSize 経由の UI ディスパッチのみ（WCF 経路の実行スレッドはホスト生成
		// 構成に依存するため、スレッドを仮定せず冒頭で InvokeRequired を弾く）、読み取りは
		// UI ディスパッチ後の EnsureBrowserSizeRestored 内（= UI スレッド）のみ行うため、
		// volatile 不要・ロック不要。
		private Size _lastKnownBrowserSize = Size.Empty;

		/// <summary>
		/// スタイルシートの変更が適用されているか
		/// </summary>
		private bool StyleSheetApplied
		{
			get { return _styleSheetApplied; }
			set
			{
				if (value)
				{
					//Browser.Anchor = AnchorStyles.None;
					ApplyZoom();
					SizeAdjuster_SizeChanged(null, new EventArgs());
				}
				else
				{
					SizeAdjuster.SuspendLayout();
					if (IsBrowserInitialized)
					{
						//Browser.Anchor = AnchorStyles.Top | AnchorStyles.Left;
						Browser.Location = new Point(0, 0);
						Browser.MinimumSize = new Size(0, 0);
						// SizeAdjuster_SizeChanged / ApplyZoom と同じ極小ガード: 最小化・非表示・ドッキング解除の
						// 窓で SizeAdjuster が 0×0・1×1 のとき、ここだけ無ガードだとコントロールが 0×0 に退化する。
						// サイズ供給源も極小なのでこの 0×0 は自力で戻らず、入力座標変換の 0 ガードが全入力を拒否する。
						if (SizeAdjuster.Width > 10 && SizeAdjuster.Height > 10)
							Browser.Size = SizeAdjuster.Size;
						RememberBrowserSize();
					}
					SizeAdjuster.ResumeLayout();
				}

				_styleSheetApplied = value;
			}
		}

		/// <summary>
		/// 適用状態の読み取り専用アクセス。座標変換はワーカースレッドで走るため、
		/// setter が持つ UI 操作の副作用を踏まないこの形でのみ参照させる。
		/// </summary>
		internal bool IsStyleSheetApplied => _styleSheetApplied;

		/// <summary>
		/// 艦これが読み込まれているかどうか
		/// </summary>
		private bool IsKanColleLoaded { get; set; }

		private VolumeManager _volumeManager = null;

		private string _lastScreenShotPath;


		private NumericUpDown ToolMenu_Other_Volume_VolumeControl
		{
			get { return (NumericUpDown)((ToolStripControlHost)ToolMenu_Other_Volume.DropDownItems["ToolMenu_Other_Volume_VolumeControlHost"]).Control; }
		}

		private PictureBox ToolMenu_Other_LastScreenShot_Control
		{
			get { return (PictureBox)((ToolStripControlHost)ToolMenu_Other_LastScreenShot.DropDownItems["ToolMenu_Other_LastScreenShot_ImageHost"]).Control; }
		}



		/// <summary>
		/// </summary>
		/// <param name="serverUri">ホストプロセスとの通信用URL</param>
		public FormBrowser(string serverUri)
		{
			InitializeComponent();

			// 拡張メニューの初期化 (タグ付けと共通イベントの適用)
			InitializeColorFilterMenu();

			ServerUri = serverUri;
			StyleSheetApplied = false;


			// 音量設定用コントロールの追加
			{
				var control = new NumericUpDown();
				control.Name = "ToolMenu_Other_Volume_VolumeControl";
				control.Maximum = 100;
				control.TextAlign = HorizontalAlignment.Right;
				control.Font = ToolMenu_Other_Volume.Font;

				control.ValueChanged += ToolMenu_Other_Volume_ValueChanged;
				control.Tag = false;

				var host = new ToolStripControlHost(control, "ToolMenu_Other_Volume_VolumeControlHost");

				control.Size = new Size(host.Width - control.Margin.Horizontal, host.Height - control.Margin.Vertical);
				control.Location = new Point(control.Margin.Left, control.Margin.Top);


				ToolMenu_Other_Volume.DropDownItems.Add(host);
			}

			// スクリーンショットプレビューコントロールの追加
			{
				double zoomrate = 0.5;
				var control = new PictureBox();
				control.Name = "ToolMenu_Other_LastScreenShot_Image";
				control.SizeMode = PictureBoxSizeMode.Zoom;
				control.Size = new Size((int)(KanColleSize.Width * zoomrate), (int)(KanColleSize.Height * zoomrate));
				control.Margin = new Padding();
				control.Image = new Bitmap((int)(KanColleSize.Width * zoomrate), (int)(KanColleSize.Height * zoomrate), PixelFormat.Format24bppRgb);
				using (var g = Graphics.FromImage(control.Image))
				{
					g.Clear(SystemColors.Control);
					g.DrawString("スクリーンショットをまだ撮影していません。\r\n", Font, Brushes.Black, new Point(4, 4));
				}

				var host = new ToolStripControlHost(control, "ToolMenu_Other_LastScreenShot_ImageHost");

				host.Size = new Size(control.Width + control.Margin.Horizontal, control.Height + control.Margin.Vertical);
				host.AutoSize = false;
				control.Location = new Point(control.Margin.Left, control.Margin.Top);

				host.Click += ToolMenu_Other_LastScreenShot_ImageHost_Click;

				ToolMenu_Other_LastScreenShot.DropDownItems.Insert(0, host);
			}

			// ダミー映像の継続警告タイマ（10 分周期）。開始時のみ Start する。
			// HeartbeatTimer と同一ライフサイクル（Exit で Stop、form Dispose で解放）。
			// 本番安全: 見落としたまま常時 ON の状態を検知するため 10 分周期で警告するだけで、状態は変えない。
			_dummyVideoWarnTimer = new System.Windows.Forms.Timer();
			_dummyVideoWarnTimer.Interval = 10 * 60 * 1000;
			_dummyVideoWarnTimer.Tick += (s, e) =>
			{
				if (_dummyVideoActive)
					AddLog(1, "[DummyVideo] ダミー映像が 10 分以上継続しています（停止すべきか確認してください）");
			};

		}


		private void FormBrowser_Load(object sender, EventArgs e)
		{
			SetWindowLong(this.Handle, GWL_STYLE, WS_CHILD);

			// ホストプロセスに接続
			BrowserHost = new PipeCommunicator<BrowserLib.IBrowserHost>(
				this, typeof(BrowserLib.IBrowser), ServerUri + "Browser", "Browser");
			BrowserHost.Connect(ServerUri + "/BrowserHost");
			BrowserHost.Faulted += BrowserHostChannel_Faulted;
	
	
			// BrowserHost.Proxy.Configuration が null の可能性があるため、null チェックを追加
			var configuration = BrowserHost.Proxy.Configuration;
			if (configuration != null)
			{
				ConfigurationChanged(configuration);
			}


			// ウィンドウの親子設定＆ホストプロセスから接続してもらう
			BrowserHost.Proxy.ConnectToBrowser(this.Handle);

			// 親ウィンドウが生きているか確認 
			HeartbeatTimer.Tick += (EventHandler)((sender2, e2) =>
			{
				BrowserHost.AsyncRemoteRun(() => { HostWindow = BrowserHost.Proxy.HWND; });
			});
			HeartbeatTimer.Interval = 2000; // 2秒ごと　
			HeartbeatTimer.Start();


			BrowserHost.AsyncRemoteRun(() => BrowserHost.Proxy.GetIconResource());


			// Note: RemoteController の初期化は、プロキシ設定付きで Cef.Initialize が実行される
			// InitializeBrowser() 内で行います。FormBrowser_Load で生成すると CefSharp の型ロードに伴い
			// Cef.Initialize がプロキシ無しのデフォルト設定で早期初期化されてしまうのを防ぎます。
		}


		/// <summary>
		/// ブラウザを初期化します。
		/// 最初の呼び出しのみ有効です。二回目以降は何もしません。
		/// </summary>
		void InitializeBrowser()
		{
			if (Browser != null)
				return;

			// [task19 不具合3修正 / Round 7 Minor 修正] 公式機構 CefSharpSettings.Proxy の設定は単一責務に集約する:
			// - Program.cs が Application.Run 以前に既定値（127.0.0.1:40620）を早期設定済み。
			// - ここで再適用するのは、ProxySettings が到着済み（SetProxy 経由で設定済み）かつ
			//   Cef.Initialize 実行前（= CefSharpSettings.Proxy の変更が有効な唯一のタイミング）の場合のみ。
			// - ProxySettings が未到着（null/空）の場合は既定値が既に Program.cs で設定済みのため再設定しない
			//   （同一値の重複設定を排除）。
			// - Cef.Initialize が既に実行済みの場合は変更が無効なためここで再適用しても意味がなく、
			//   後続の Cef.IsInitialized == true 分岐で警告ログを出力する。
			if (Cef.IsInitialized == false && !string.IsNullOrEmpty(ProxySettings))
				ApplyCefSharpProxy(ProxySettings);

			// Cef.Initialize はプロセス内で一度しか呼べないので、既に初期化済みか確認する
			if (Cef.IsInitialized == false)
			{
				var settings = new CefSettings()
				{
					BrowserSubprocessPath = Path.Combine(
							AppDomain.CurrentDomain.SetupInformation.ApplicationBase,
							Environment.Is64BitProcess ? "x64" : "x86",
							"CefSharp.BrowserSubprocess.exe"),
					CachePath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), BrowserCachePath),
					Locale = "ja",
					AcceptLanguageList = "ja,en-US,en",        // todo: いる？
					LogSeverity = Configuration.SavesBrowserLog ? LogSeverity.Error : LogSeverity.Disable,
					LogFile = Configuration.SavesBrowserLog
						? Path.Combine(AppDomain.CurrentDomain.SetupInformation.ApplicationBase, "BrowserLog.log")
						: string.Empty,
				};

				if (!Configuration.HardwareAccelerationEnabled)
						settings.DisableGpuAcceleration();
					else
					{
						// task28: WebCodecs API 用の GPU ハードウェアエンコード有効化
						// Intel QSV / NVENC 等の GPU ハードウェアエンコーダに処理をオフロードするため
						// 設計書 §5.2 対応: AcceleratedVideoEncoder,AcceleratedVideoDecoder (Chromium 143 以降の公式フラグ)
						// 【レビュー修正】 GPU無効時はフラグ追加をスキップ（一次レビュー Major 対応）
						settings.CefCommandLineArgs.Add("enable-features", "AcceleratedVideoEncoder,AcceleratedVideoDecoder");
					}

				// [task19 不具合3修正] proxy-server は公式機構 CefSharpSettings.Proxy により適用する。
				// ApplyCefSharpProxy（Cef.Initialize 前）が --proxy-server をコマンドラインに反映する。
				// 手動の settings.CefCommandLineArgs.Add("proxy-server") は削除する（重複キー例外の回避）。
				if (Configuration.ForceColorProfile)
					settings.CefCommandLineArgs.Add("force-color-profile", "srgb");

				// --------------------------------------------------------------------------------------------
				// 遠隔操作時のために、バックグラウンド（最小化や他のウィンドウに隠れた状態）
				// でも描画・JavaScriptを停止・制限させない設定
				// --------------------------------------------------------------------------------------------
				settings.CefCommandLineArgs.Add("disable-background-timer-throttling", "1");
				settings.CefCommandLineArgs.Add("disable-backgrounding-occluded-windows", "1");
				settings.CefCommandLineArgs.Add("disable-renderer-backgrounding", "1");
				// Chrome 87以降で導入された ネイティブウィンドウの隠蔽検知 による描画停止を無効化
				settings.CefCommandLineArgs.Add("disable-features", "CalculateNativeWinOcclusion,RendererBackgrounding");

				CefSharpSettings.SubprocessExitIfParentProcessClosed = true;
				Cef.Initialize(settings, false, (IBrowserProcessHandler)null);
			}
			else
			{
				// [task19 不具合3修正] CefSharp が既に初期化済み（Cef.IsInitialized == true）の場合の検知
				// この場合、Cef.Initialize が呼ばれないため proxy-server が適用されず、
				// ゲーム API トラフィックが APIObserver プロキシ（ポート40620）に到達しない。
				// 自動再起動は行わない（ユーザーの進行中のセッションを壊さない）。
				// 警告ログを出力し、ユーザーに EOBrowser 再起動が必要であることを伝える。
				AddLog(2, "[FormBrowser] CefSharp が既に初期化済みです (Cef.IsInitialized == true)。proxy-server が適用されていないため、ゲーム API トラフィックが APIObserver プロキシ（ポート40620）に到達しない可能性があります。EOBrowser を再起動してください。");
			}

			// キャプチャ pacing（Task.Delay）の分解能を 1ms 級へ引き上げる。
			// 既定のタイマー粒度（約 15.6ms）では 16ms 級のフレーム間隔制御が粗くなり、
			// 絶対アンカー pacing でも待機の過不足が残る。解除は Exit() で対して行う。
			if (!_timerResolutionRaised)
			{
				_timerResolutionRaised = true;
				TimeBeginPeriod(1);
			}


			var requestHandler = new CustomRequestHandler(pixiSettingEnabled: Configuration.PreserveDrawingBuffer);
			requestHandler.RenderProcessTerminated += (mes) => AddLog(3, mes);

			Browser = new ChromiumWebBrowser(@"about:blank")
			{
				Dock = DockStyle.None,
				Size = SizeAdjuster.Size,
				RequestHandler = requestHandler,
				MenuHandler = new MenuHandler(),
				KeyboardHandler = new KeyboardHandler(),
				DragHandler = new DragHandler(),
			};
			Browser.LoadingStateChanged += Browser_LoadingStateChanged;
			Browser.FrameLoadStart += Browser_OnFrameLoadStart;
			Browser.FrameLoadEnd += Browser_FrameLoadEnd;
			Browser.IsBrowserInitializedChanged += Browser_IsBrowserInitializedChanged;
			SizeAdjuster.Controls.Add(Browser);
			RememberBrowserSize();

			// 遠隔操作サーバーの起動（Cef.Initialize 完了後に安全に生成）
			if (_remoteController == null)
			{
				try
				{
					int port = Configuration.RemoteControllerPort;
					if (port <= 0 || port > 65535) port = 8080;
					string address = string.IsNullOrEmpty(Configuration.RemoteControllerAddress) ? "*" : Configuration.RemoteControllerAddress;
					string password = Configuration.RemoteControllerPassword;

					// task28: WebCodecs 設定値を取得
					int webCodecsBitrate = (int)Configuration.WebCodecsBitrate;
					if (webCodecsBitrate <= 0) webCodecsBitrate = 800000;
					int webCodecsFps = (int)Configuration.WebCodecsFps;
					if (webCodecsFps <= 0) webCodecsFps = 30;

					AddLog(2, $"[FormBrowser] RemoteController の初期化を開始します (Address: {address}, Port: {port})");

					lock (_hudBufferLock)
					{
						_remoteController = new RemoteController(this, port, address, password, webCodecsBitrate, webCodecsFps);
						_remoteController.Start();

						// 表示状態監視へこのウィンドウハンドルを登録（WM_WTSSESSION_CHANGE を WndProc 経由で転送）
						_remoteController.AttachDisplayStateMonitor(this.Handle);

						if (_bufferedHudJson != null)
						{
							_remoteController.UpdateHudData(_bufferedHudJson);
							_bufferedHudJson = null;
						}
					}
				}
				catch (Exception ex)
				{
					AddLog(3, $"[FormBrowser] RemoteController 初期化エラー: {ex.Message}");
				}
			}
		}


		/// <summary>
		/// [task19 不具合3修正] 公式機構 <c>CefSharpSettings.Proxy</c> によるプロキシ設定を行う。
		/// </summary>
		/// <remarks>
		/// <c>CefSharpSettings.Proxy</c> は静的プロパティで、<c>Cef.Initialize</c> 前に設定すると
		/// CefSharp がコマンドライン引数 <c>--proxy-server</c> を自動生成し、
		/// Chromium のネットワークスタック（utility-network / renderer サブプロセス）へ反映する。
		/// <c>Cef.Initialize</c> 実行後の変更は無効なため、本メソッドは初期化前に呼ぶこと。
		/// 冪等性：同一値で繰り返し設定しても安全（静的プロパティの上書き）。
		/// [Round 7 Minor 修正]
		/// - IPv6 リテラルに対応（例 "[::1]:40620"、 "::1:40620"）。ホストはブラケット付きで
		///   保持し、Chromium の "--proxy-server=[::1]:40620" 標準形式になるよう生成する。
		/// - ポート番号は 1〜65535 の範囲を検証する（無効値・未パース時は既定値にフォールバックしログ記録）。
		/// </remarks>
		/// <param name="proxy">"host:port" 形式（例 "127.0.0.1:40620"、"[::1]:40620"）のプロキシアドレス。
		/// null / 空 / 未パース / ポート範囲無効の場合は既定値 127.0.0.1:40620 にフォールバックする。</param>
		public static void ApplyCefSharpProxy(string proxy)
		{
			string host = DefaultApiObserverHost;
			string port = DefaultApiObserverPort;

			// "host:port" 形式を host / port に分解（ProxyOptions は IP/Port とも string 型）
			if (!string.IsNullOrEmpty(proxy))
			{
				var trimmed = proxy.Trim();
				string parsedHost = null;
				string parsedPort = null;

				// [Round 7 Minor 修正] IPv6 対応パース:
				//  - ブラケット付き IPv6 リテラル "[::1]:40620"（ホストはブラケット付きで保持）
				//  - IPv4 / ホスト名 "127.0.0.1:40620"（ホストにコロンを含まない通常形式）
				var m = System.Text.RegularExpressions.Regex.Match(trimmed, @"^(?:\[(?<b6>[0-9A-Fa-f:.]+)\]|(?<h>[^:\]]+)):(?<p>\d+)$");
				if (m.Success)
				{
					parsedHost = m.Groups["b6"].Success ? ("[" + m.Groups["b6"].Value + "]") : m.Groups["h"].Value;
					parsedPort = m.Groups["p"].Value;
				}
				else if (trimmed.Contains("::"))
				{
					// ブラケットなし IPv6 リテラル "::1:40620"（ホストにコロンを含む場合）:
					// 最後の ':' をポート区切りとして分割し、ホストはブラケット付きで保持する
					int idx = trimmed.LastIndexOf(':');
					if (idx > 0)
					{
						var h = trimmed.Substring(0, idx);
						var p = trimmed.Substring(idx + 1);
						if (!string.IsNullOrEmpty(h) && p.All(char.IsDigit))
						{
							parsedHost = "[" + h + "]";
							parsedPort = p;
						}
					}
				}

				if (parsedHost != null)
				{
					int portVal;
					// [Round 7 Minor 修正] ポート番号を 1〜65535 の範囲で検証する
					// （従来の p.All(char.IsDigit) は数値範囲を検証せず、port=0 や 65535 超過が受け入れられていた）
					if (int.TryParse(parsedPort, out portVal) && portVal >= 1 && portVal <= 65535)
					{
						host = parsedHost;
						port = portVal.ToString();
					}
					else
					{
						// ポート範囲無効（0 または 65535 超過）: 既定値へフォールバックしログ記録
						System.Diagnostics.Trace.WriteLine($"[FormBrowser] ApplyCefSharpProxy: \"{trimmed}\" のポートが範囲外(1-65535)のため、既定値 {DefaultApiObserverHost}:{DefaultApiObserverPort} を使用します。");
					}
				}
				else if (trimmed.All(char.IsDigit))
				{
					// ポート番号のみ（例 "40620"）
					int portVal;
					if (int.TryParse(trimmed, out portVal) && portVal >= 1 && portVal <= 65535)
					{
						port = portVal.ToString();
					}
					else
					{
						// ポート範囲無効: 既定値を維持しログ記録
						System.Diagnostics.Trace.WriteLine($"[FormBrowser] ApplyCefSharpProxy: \"{trimmed}\" のポートが範囲外(1-65535)のため、既定値 {DefaultApiObserverHost}:{DefaultApiObserverPort} を使用します。");
					}
				}
				else if (!trimmed.Contains(":"))
				{
					// ホスト名のみ（稀）: 既定のポートを維持
					host = trimmed;
				}
				// それ以外（例 ":40620" のような未パース形式）: 既定値を維持
			}

			// Cef.Initialize 前に設定する必要がある（公式機構）。
			// 冪等に設定（同一値の上書きは安全）。
			CefSharpSettings.Proxy = new ProxyOptions(host, port);
		}


		// ---- Windows multimedia timer（winmm）----
		// Task.Delay / Thread.Sleep の分解能は既定では 15.6ms であり、WCS キャプチャループの
		// 絶対アンカー pacing（目標間隔 16ms 級）では待機が過不足なく効かず実効 FPS が不安定になる。
		// timeBeginPeriod(1) で 1ms 級へ引き上げる。対応する timeEndPeriod は Exit() で必ず対にして呼び出す。
		[DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
		private static extern uint TimeBeginPeriod(uint milliseconds);

		[DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
		private static extern uint TimeEndPeriod(uint milliseconds);

		// timeBeginPeriod の多重要求防止（Cef.IsInitialized 済み経路でも 1 回だけ引き上げる）
		private static bool _timerResolutionRaised = false;

		void Exit()
		{
			if (!BrowserHost.Closed)
			{
				BrowserHost.Close();
				HeartbeatTimer.Stop();
				_dummyVideoWarnTimer.Stop();
				_remoteController?.Dispose();
				// [task21] 空白ページ配信用 static ストアの全エントリを解放（一次 Major 2 / S-1 対応）。
				// Cef.Shutdown() は同期停止のため直前呼び出しで安全（設計書 §4.1 注記）。
				CustomResourceRequestHandler.ClearLogoutHtmlStore();
				// ダミー映像のメモリ配信ストアも対で解放（Exit 以外の経路では StopDummyVideo 側で解放済み、
				// 二重解放とならないよう Clear は null 代入のみで冪等）
				CustomResourceRequestHandler.ClearDummyVideoHtml();
				// multimedia timer の分解能引き上げはプロセス終了まで有効なため、解除は対で行う
				if (_timerResolutionRaised)
				{
					_timerResolutionRaised = false;
					TimeEndPeriod(1);
				}
				Cef.Shutdown();
				Application.Exit();
			}
		}

		void BrowserHostChannel_Faulted(Exception e)
		{
			// 親と通信できなくなったら終了する
			Exit();
		}

		public void CloseBrowser()
		{
			HeartbeatTimer.Stop();
			// リモートコールでClose()呼ぶのばヤバそうなので非同期にしておく
			BeginInvoke((Action)(() => Exit()));
		}

		public void ConfigurationChanged(BrowserLib.BrowserConfiguration conf)
		{
			// WCF サービス経由で呼び出される可能性があるため、UI スレッドにディスパッチする
			if (this.InvokeRequired)
			{
				this.BeginInvoke((Action)(() => ConfigurationChanged(conf)));
				return;
			}

			Configuration = conf;

			SizeAdjuster.AutoScroll = Configuration.IsScrollable;
			ToolMenu_Other_Zoom_Fit.Checked = Configuration.ZoomFit;
			ApplyZoom();
			ToolMenu_Other_AppliesStyleSheet.Checked = Configuration.AppliesStyleSheet;
			ToolMenu.Dock = (DockStyle)Configuration.ToolMenuDockStyle;
			ToolMenu.Visible = Configuration.IsToolMenuVisible;
			ToolMenu_Other_ClearCache.Visible = conf.EnableDebugMenu;

			UpdateColorFilterMenu();
			ApplyColorFilter();

			// 2026/07/23 追記（遠隔操作サーバー設定の動的更新）
			// 注意: _remoteController がまだ生成されていない（null）場合、
			// ConfigurationChanged は RemoteController 構築前に発生し得るため、
			// ガード句で安全に処理する。
			if (_remoteController != null)
			{
				_remoteController.UpdatePassword(conf.RemoteControllerPassword);
			}
		}

		private void ConfigurationUpdated()
		{
			BrowserHost.AsyncRemoteRun(() => BrowserHost.Proxy.ConfigurationUpdated(Configuration));
		}

		/// <summary>
		/// リモートコントローラーからログインページへ移動します。
		/// </summary>
		public void NavigateToLoginPage()
		{
			// [task20] WinForms クロススレッド対策（r3 指摘3 対応）。
			// HandleControlAsync（HTTP リスナーワーカースレッド）から直接呼ばれるため、
			// InvokeRequired チェックで UI スレッドへディスパッチする（RefreshBrowser と同パターン）。
			// 従来はチェックなしで Navigate → StyleSheetApplied setter（UI スレッド制約）が
			// HTTP スレッド上実行され InvalidOperationException のリスクがあった。
			if (this.InvokeRequired)
			{
				this.BeginInvoke((Action)(() => NavigateToLoginPage()));
				return;
			}

			// [task20] null・空文字列・スキーム防御を ResolveLogInPageURL() で統一
			//（従来は Configuration.LogInPageURL の裸参照。正常時の遷移 URL は不変）
			Navigate(ResolveLogInPageURL());
		}

		/// <summary>
		/// リモートコントローラーからプライバシーフィルターモードを設定します。
		/// </summary>
		public void SetFilterMode(int mode)
		{
			Configuration.ColorFilterMode = mode;
			ApplyColorFilter();
			ConfigurationUpdated();
		}

		internal void AddLog(int priority, string message)
		{
			BrowserHost.AsyncRemoteRun(() => BrowserHost.Proxy.AddLog(priority, message));
		}

		internal void SendErrorReport(string exceptionName, string message)
		{
			BrowserHost.AsyncRemoteRun(() => BrowserHost.Proxy.SendErrorReport(exceptionName, message));
		}


		public void InitialAPIReceived()
		{
			// 再ログイン復帰でも論理フォーカスは戻らない（ゲーム側の操作で失われる）。
			// 有効/無効はこのメソッドの冒頭ゲートで一括判定するため、ここでは条件を書かない。
			EnsureInputFocus("relogin");

			IsKanColleLoaded = true;
			// [task22] ゲーム API 受信 = ゲームがロード済み（再ログイン後に再発火するため復帰検出にも機能）
			_isLogoutBlank = false;
			
			//ロード直後の適用ではレイアウトがなぜか崩れるのでこのタイミングでも適用
			ApplyStyleSheet();
			ApplyZoom();
			ApplyColorFilter();
			DestroyDMMreloadDialog();

			//起動直後はまだ音声が鳴っていないのでミュートできないため、この時点で有効化
			SetVolumeState();
		}

		/// <summary>
		/// リモート操作の入力が届くよう、CEF の論理フォーカスだけを取り直す（OS ウィンドウの活性化はしない）。
		/// 合成マウス入力は論理フォーカスが切れていると無音で捨てられるため、
		/// 入力バースト・再ログイン復帰の両地点から呼んで復元する。
		/// SetFocus は CEF が既にフォーカスを持つ場合は無害なので、呼び出しが重複しても過剰な実行は問題にならない。
		/// </summary>
		/// <param name="reason">focus_state ログに残す復元理由（relogin / input_burst 等）</param>
		public void EnsureInputFocus(string reason)
		{
			if (this.InvokeRequired)
			{
				// WS/HTTP ワーカーから呼ばれるため、以下は UI スレッドで実行する
				this.BeginInvoke((Action)(() => EnsureInputFocus(reason)));
				return;
			}

			// 論理フォーカス復元の有効/無効はここで一括判定する（呼び出し側ごとに条件を書かない）。
			// オフ時は SetFocus も focus_state 記録も行わない完全な no-op になる。
			// Configuration はホストとの接続確立前は null になり得る（設定未確定 = 操作しない）。
			if (Configuration == null || !Configuration.RemoteInputFocusRecovery)
				return;
			if (Browser == null || !Browser.IsBrowserInitialized)
				return;

			// UI スレッドで再確認（WS/HTTP スレッドでの呼出は粗い前フィルタとみなす）
			if (!Browser.Focused)
			{
				var host = Browser.GetBrowser()?.GetHost();
				if (host == null)
					return;
				// CefSharp の IBrowserHost.SetFocus(true) は CEF UI スレッドへ自己 post するため
				// 任意スレッドから安全（windowed レンダリングの正規 API・SendFocusEvent は OSR 用で使わない）。
				// OS 活性化（SetForegroundWindow 等）は合成入力経路の必要条件でなく
				// ホストウィンドウ奪取の副作用を伴うため行わない。
				host.SetFocus(true);
				// RC 未起動時は focus_state 記録だけがスキップされ、復元自体は行われる（安全な no-op）
				_remoteController?.AppendFocusState("focus_restored_setfocus", reason);
			}
		}


		private void SizeAdjuster_SizeChanged(object sender, EventArgs e)
		{
			// 最小化や非表示時はブラウザコントロールのサイズ変更をスキップする
			// 理由：最小化やドッキング解除による一時的な非表示などを行うと、SizeAdjusterのサイズが
			//       極小（0×0や1×1など）になり、Browser.Sizeも連動して極小化される。
			//       これによりCEFのビューポートも縮小され、リモート操作(RemoteController)の
			//       SendMouseClickEventに渡す座標がビューポート外となり、クリックが一切効かなくなる。
			//       そのため、極小サイズ時はサイズ変更処理を行わず、以前の正常なサイズを維持する。
			if (SizeAdjuster.Width <= 10 || SizeAdjuster.Height <= 10)
				return;

			if (!StyleSheetApplied)
			{
				if (Browser != null)
				{
					Browser.Location = new Point(0, 0);
					Browser.Size = SizeAdjuster.Size;
					RememberBrowserSize();
				}
				return;
			}

			ApplyZoom();
		}

		// 設定後のコントロール実寸を記録する（MinimumSize 等にクリップされた値でも目に見える実寸と一致する）。
		// 極小窓（最小化時など）の値は有効サイズとして採用しない。
		private void RememberBrowserSize()
		{
			if (this.InvokeRequired)
			{
				// WCF サービスコール経路（InitialAPIReceived の inline ApplyZoom、Navigate /
				// RefreshBrowser / ApplyStyleSheet のプロキシ経路）等の UI 保証が無い経路から届く。
				// WCF 経路の実行スレッドは SynchronizationContext ディスパッチ（既定ではホストを
				// オープンしたスレッド）で決まりホスト生成構成に依存するため、呼出スレッドを仮定しない。
				// UI スレッドへディスパッチして書込を一箇所に寄せる（EnsureInputFocus と同一パターン）。
				// ハンドル未生成・メッセージスレッド停止中は BeginInvoke が例外を投じうる（MS Learn
				// Control.BeginInvoke: 適切なウィンドウハンドルが見つからない場合 InvalidOperationException、
				// メッセージ処理スレッド停止時も例外）。この窓で 1 件記録が飛んでも無害
				// （本キャッシュは保険であり、ハンドル生成後の正常なサイズ設定経路で再記録される）。
				// 例外を WCF サービスメソッドまで伝播させるとチャネルが Fault になりホスト側で
				// 誤った「ブラウザプロセス終了」報告になりうるため、ログ付きで局所的に握りつぶす。
				try
				{
					this.BeginInvoke((Action)(RememberBrowserSize));
				}
				catch (Exception ex)
				{
					AddLog(2, $"[SizeCache] サイズ記録の UI ディスパッチに失敗したためスキップしました: {ex.GetType().Name} {ex.Message}");
				}
				return;
			}
			if (Browser == null)
				return;
			if (Browser.Width > 10 && Browser.Height > 10)
				_lastKnownBrowserSize = new Size(Browser.Width, Browser.Height);
		}

		// ウィンドウが最小化・非表示の窓ではサイズ供給源（SizeAdjuster）自体が極小で、
		// 通常のサイズ設定経路はすべて極小ガードにより no-op のため、極小化したコントロールは自力で戻れない
		// （入力座標変換は寸法 0 ガード ≤0 で全入力を座標変換前に拒否する）。ゲーム読込確認時点で直前の有効サイズへ戻す。
		// 正常時（コントロール >10）は必ず no-op。
		private void EnsureBrowserSizeRestored(string context)
		{
			if (this.InvokeRequired)
			{
				// FrameLoadEnd（CEF イベントスレッド）・WCF サービスコール経路（InitialAPIReceived）等
				// の UI 保証が無い経路から呼ばれる（WCF 経路の実行スレッドはホスト生成構成に依存し
				// 断定しない・RememberBrowserSize と同じ根拠）。以下は UI スレッドで実行する
				// （EnsureInputFocus と同一パターン）。
				// ハンドル未生成・メッセージスレッド停止中は BeginInvoke が例外を投じうる（MS Learn
				// Control.BeginInvoke）。例外が WCF サービスメソッドまで伝播するとチャネルが Fault に
				// なりホスト側で誤った「ブラウザプロセス終了」報告になりうるため、ログ付きで局所的に
				// 握りつぶす。復元 1 回の欠落は無害 — 次の復元トリガーで再試行され、窓が復元すれば
				// 正規のサイズ設定経路が正しい寸法へ設定する。
				try
				{
					this.BeginInvoke((Action)(() => EnsureBrowserSizeRestored(context)));
				}
				catch (Exception ex)
				{
					AddLog(2, $"[SizeRestore] サイズ復元の UI ディスパッチに失敗したためスキップしました: {ex.GetType().Name} {ex.Message}");
				}
				return;
			}
			if (Browser == null || !IsBrowserInitialized)
				return;
			if (Browser.Width > 10 && Browser.Height > 10)
				return;
			if (_lastKnownBrowserSize.Width <= 10 || _lastKnownBrowserSize.Height <= 10)
				return;

			Browser.Size = _lastKnownBrowserSize;
			AddLog(1, string.Format("[SizeRestore] Browser コントロールが極小でした（context={0}）。直前の有効サイズ {1}x{2} に復元しました", context, _lastKnownBrowserSize.Width, _lastKnownBrowserSize.Height));
			RememberBrowserSize();
		}

		private void CenteringBrowser()
		{
			if (SizeAdjuster.Width == 0 || SizeAdjuster.Height == 0) return;
			int x = Browser.Location.X, y = Browser.Location.Y;
			bool isScrollable = Configuration.IsScrollable;

			if (!isScrollable || Browser.Width <= SizeAdjuster.Width)
			{
				x = (SizeAdjuster.Width - Browser.Width) / 2;
			}
			if (!isScrollable || Browser.Height <= SizeAdjuster.Height)
			{
				y = (SizeAdjuster.Height - Browser.Height) / 2;
			}

			//if ( x != Browser.Location.X || y != Browser.Location.Y )
			Browser.Location = new Point(x, y);
		}


		// ---------------------------------------------------------
		// LoadingStateChanged（DOM 完成後の確実な再適用）
		// ---------------------------------------------------------
		private void Browser_LoadingStateChanged(object sender, LoadingStateChangedEventArgs e)
		{
			if (e.IsLoading)
				return;

			BeginInvoke((Action)(() =>
			{
				try
				{
					// DOM が完成した後に確実に適用
					ApplyStyleSheet();
					ApplyZoom();
					ApplyColorFilter();
					DestroyDMMreloadDialog();
				}
				catch (Exception ex)
				{
					SendErrorReport(ex.ToString(), "LoadingStateChanged: 再適用に失敗しました。");
				}
			}));
		}


		internal bool IsBrowserInitialized =>
			Browser != null &&
			Browser.IsBrowserInitialized;

		internal IFrame GetMainFrame()
		{
			if (!IsBrowserInitialized)
				return null;

			var browser = Browser.GetBrowser();
			var frame = browser.MainFrame;

			if (IsMainFrame(frame))
				return frame;

			return null;
		}

		internal IFrame GetGameFrame()
		{
			if (!IsBrowserInitialized)
				return null;

			var browser = Browser.GetBrowser();
			foreach (var id in browser.GetFrameIdentifiers())
			{
				var frame = browser.GetFrameByIdentifier(id);
				if (IsGameFrame(frame))
					return frame;
			}
			return null;
		}

		internal IFrame GetKanColleFrame()
		{
			if (!IsBrowserInitialized)
				return null;

			var browser = Browser.GetBrowser();
			foreach (var id in browser.GetFrameIdentifiers())
			{
				var frame = browser.GetFrameByIdentifier(id);
				if (IsKanColleFrame(frame))
					return frame;
			}
			return null;
		}



		private bool IsMainFrame(IFrame frame)
		{
			if (frame == null || string.IsNullOrEmpty(frame.Url)) return false;

			try
			{
				var url = frame.Url;

				// DMM のメインページはほぼ必ずこのどれか
				return url.Contains("dmm.com/netgame") ||
					   url.Contains("dmm.com/play") ||
					   url.Contains("games.dmm.com") ||
					   url.Contains("www.dmm.com/netgame");
			}
			catch
			{
				return false;
			}
		}


		private bool IsGameFrame(IFrame frame)
		{
			if (frame == null || string.IsNullOrEmpty(frame.Url)) return false;

			try
			{
				var uri = new Uri(frame.Url);

				// KanColle のゲームフレームは必ず /gadgets/ifr で始まる
				return uri.Host.Contains("osapi.dmm.com") &&
					   uri.AbsolutePath.StartsWith("/gadgets/ifr");
			}
			catch
			{
				return false;
			}
		}

		private bool IsKanColleFrame(IFrame frame)
		{
			if (frame == null || string.IsNullOrEmpty(frame.Url)) return false;

			try
			{
				var uri = new Uri(frame.Url);

				return uri.AbsolutePath.Contains("/kcs2/index.php");
			}
			catch
			{
				return false;
			}
		}

		// キャプチャ経路（中央ループ/WCS 系/統計読み取り/watchdog/セットアップ操作）が使うフレーム解決。
		// game iframe（/kcs2/index.php）とダミー映像ページの 2 値排他を返す。
		// GetKanColleFrame() はゲーム固有用途（プライバシーフィルタ・スクリーンショット）のため
		// 改名・意味変更しない（ダミー映像をそれらの対象にしない隔離方針）。
		internal IFrame GetCaptureSourceFrame()
		{
			if (!IsBrowserInitialized)
				return null;

			var browser = Browser.GetBrowser();
			foreach (var id in browser.GetFrameIdentifiers())
			{
				var frame = browser.GetFrameByIdentifier(id);
				if (IsCaptureSourceFrame(frame))
					return frame;
			}
			return null;
		}

		// キャプチャエンジン注入・全キャプチャ経路のフレーム判定。
		// game iframe とダミー映像ページのみ一致の 2 値排他であり、それ以外のページ
		//（DMM 本体・ログアウト空白・about:blank 等）では false として
		// 既存の「映像源なし=503」挙動を維持する（外部ページのキャプチャ源偽装の防止は
		// IsDummyVideoUrl の厳密判定に委ねる）。
		private bool IsCaptureSourceFrame(IFrame frame)
		{
			return IsKanColleFrame(frame) || (frame != null && CustomResourceRequestHandler.IsDummyVideoUrl(frame.Url));
		}

		/// <summary>
		/// スタイルシートを適用します。
		/// </summary>
		public void ApplyStyleSheet()
		{
			if (!IsBrowserInitialized)
				return;

			if (!Configuration.AppliesStyleSheet && !RestoreStyleSheet)
				return;

			try
			{
				var mainframe = GetMainFrame();
				var gameframe = GetGameFrame();

				// --- Restore ---
				if (RestoreStyleSheet)
				{
					mainframe?.EvaluateScriptAsync(RestoreScript);
					gameframe?.EvaluateScriptAsync(RestoreScript);

					StyleSheetApplied = false;
					RestoreStyleSheet = false;
					return;
				}

				// --- Apply ---
				// MainFrame は先行適用（v140 対策）
				if (mainframe != null)
				{
					mainframe.EvaluateScriptAsync(PageScript);
				}

				// GameFrame が取れた時点で「完成」
				if (gameframe != null)
				{
					gameframe.EvaluateScriptAsync(FrameScript)
						.ContinueWith(_ =>
						{
							BeginInvoke((Action)(() =>
							{
								StyleSheetApplied = true;
								ApplyZoom();
							}));
						});
				}
				else if (!StyleSheetApplied)
				{
					LogStyleSheetNotApplied();
				}
			}
			catch (Exception ex)
			{
				SendErrorReport(ex.ToString(), "ApplyStyleSheet: スタイルシート適用に失敗しました。");
			}
		}

		private BrowserPrivacyFilterManager _privacyFilterManager;
		internal BrowserPrivacyFilterManager PrivacyFilterManager => _privacyFilterManager ?? (_privacyFilterManager = new BrowserPrivacyFilterManager(this));

		/// <summary>
		/// プライバシーフィルタ(カラーフィルタや目隠し設定等)を適用または解除します。
		/// </summary>
		public void ApplyColorFilter()
		{
			PrivacyFilterManager.ApplyFilter(Configuration.ColorFilterMode, Configuration.AppliesHoverCover);
		}

		private void InitializeColorFilterMenu()
		{
			// UIアイテムにモード番号をTagとして持たせ、排他選択のイベントハンドラを登録する
			ToolMenu_ColorFilter_None.Tag = 0;
			ToolMenu_ColorFilter_Grayscale.Tag = 1;
			ToolMenu_ColorFilter_LowContrast.Tag = 4;

			var items = new[] {
				ToolMenu_ColorFilter_None,
				ToolMenu_ColorFilter_Grayscale,
				ToolMenu_ColorFilter_LowContrast
			};

			foreach (var item in items)
			{
				item.Click += ToolMenu_ColorFilter_Item_Click;
			}

			// カバー機能（ホバー時のみ表示）は独立のトグルスイッチとして別登録
			ToolMenu_ColorFilter_HoverCover.Click += ToolMenu_ColorFilter_HoverCover_Click;
		}

		private void ToolMenu_ColorFilter_Item_Click(object sender, EventArgs e)
		{
			if (sender is ToolStripMenuItem item && item.Tag is int mode)
			{
				Configuration.ColorFilterMode = mode;
				UpdateColorFilterMenu();
				ApplyColorFilter();
				ConfigurationUpdated();
			}
		}

		private void ToolMenu_ColorFilter_HoverCover_Click(object sender, EventArgs e)
		{
			Configuration.AppliesHoverCover = ToolMenu_ColorFilter_HoverCover.Checked;
			ApplyColorFilter(); // ColorFilter処理も再評価・再適用する可能性があるため共通メソッドを呼ぶ
			ConfigurationUpdated();
		}

		private void UpdateColorFilterMenu()
		{
			ToolMenu_ColorFilter_None.Checked = (Configuration.ColorFilterMode == 0);
			ToolMenu_ColorFilter_Grayscale.Checked = (Configuration.ColorFilterMode == 1);
			ToolMenu_ColorFilter_LowContrast.Checked = (Configuration.ColorFilterMode == 4);

			ToolMenu_ColorFilter_HoverCover.Checked = Configuration.AppliesHoverCover;
		}



		/// <summary>
		/// DMMによるページ更新ダイアログを非表示にします。
		/// </summary>
		public void DestroyDMMreloadDialog()
		{
			if (!IsBrowserInitialized)
				return;

			if (!Configuration.IsDMMreloadDialogDestroyable)
				return;

			try
			{
				var mainframe = GetMainFrame();
				if (mainframe == null)
					return;

				mainframe.EvaluateScriptAsync(DMMScript);
			}
			catch (Exception ex)
			{
				SendErrorReport(ex.ToString(), "DMMによるページ更新ダイアログの非表示に失敗しました。");
			}

		}


		// ---------------------------------------------------------
		// FrameLoadStart（早期注入）
		// ---------------------------------------------------------
		private void Browser_OnFrameLoadStart(object sender, FrameLoadStartEventArgs e)
		{
			if (Configuration == null)
				return;

			try
			{
				// メインフレーム → PageScript（DOM 安全版）
				if (IsMainFrame(e.Frame))
				{
					e.Frame.ExecuteJavaScriptAsync(PageScript);
				}

				// ゲームフレーム → FrameScript（DOM 安全版）
				if (IsGameFrame(e.Frame))
				{
					e.Frame.ExecuteJavaScriptAsync(FrameScript);
				}

				// confirm() 無効化（DOM 安全版）
				if (Configuration.IsDMMreloadDialogDestroyable && e.Frame.IsMain)
				{
					e.Frame.ExecuteJavaScriptAsync(OverrideReloadDialogScript);
				}
			}
			catch (Exception ex)
			{
				SendErrorReport(ex.ToString(), "FrameLoadStart: スクリプト注入に失敗しました。");
			}
		}


		// タイミングによっては(特に起動時)、ブラウザの初期化が完了する前に Navigate() が呼ばれることがある
		// その場合ロードに失敗してブラウザが白画面でスタートしてしまう（手動でログインページを開けば続行は可能だが）
		// 応急処置として失敗したとき後で再試行するようにしてみる
		private string navigateCache = null;
		// [task20] ログアウト（空白ページ）の保留フラグ
		// volatile 修飾子を付与（一次レビュー Critical 3 / 二次レビューで Major に降格・推奨）。
		// 書き込み・読み出しは原則 UI スレッド上のみ（LogoutToBlankPage は InvokeRequired チェックで
		// UI スレッドディスパッチ済み、クリアは Browser_IsBrowserInitializedChanged = UI スレッド）だが、
		// 将来の呼び出し経路増強に対する可視性保証として volatile を付与する。
		private volatile bool _pendingLogoutBlank = false;
		// [task22] ログアウト空白ページ「状態」フラグ（_pendingLogoutBlank = 保留要求、本フラグ = 現在状態）。
		// volatile 修飾:
		//  - InitialAPIReceived は WCF プロキシスレッドから書き込むため可視性保証が必要。
		//  - Browser_FrameLoadEnd は WinForms UI スレッド上を直接保証しない（ChromiumWebBrowser は
		//    WCF プロキシ経由でイベントを受け取る構造）ため、他スレッドから読み書きされる状態として扱う。
		//  - .NET 公式 volatile 文書（MS Learn、2026-08-19 再検証）: 「多くのシナリオでは Interlocked /
		//    lock を推奨」+「マルチプロセッサ環境では volatile の読み書きが他プロセッサへの即時可視化・
		//    最新値取得を保証しない」注記。bool は volatile を適用可能な型。
		//  - **設計上の対抗措置**: 各セットポイントは**単一の読み書き**のみを行う（複合操作なし）。
		//    状態の正しさは「単一読みの即時性」ではなく「/login-state ポーリング（≤5 秒で収束）」で
		//    保証する。この方式では volatile が十分（bool の単一読み書きには追加の原子性保証を要しない）。
		//    将来 read-modify-write 等の複合操作が生じた場合は System.Threading.Interlocked / lock に変更する。
		// セットポイント: LogoutToBlankPage(true) / Navigate(false) / InitialAPIReceived(false) /
		// Browser_FrameLoadEnd(URL 監視セーフティネット)。
		private volatile bool _isLogoutBlank = false;

		/// <summary>[task22] 本体ブラウザが現在ログアウト空白ページ状態かどうか（RemoteController が HTTP スレッドから読む。volatile 読み込み）。</summary>
		public bool IsLogoutBlank => _isLogoutBlank;

		// ダミー映像「現在状態」フラグ（_isLogoutBlank と同一の volatile 単一読み書き方式）。
		// UI スレッドでのみ書替（Start/Stop は InvokeRequired で自己ディスパッチ）。
		// HTTP/WS スレッドは IsDummyVideo で読み取るのみ（/login-state・/capture-config 収束）。
		private volatile bool _dummyVideoActive = false;

		/// <summary>キャプチャ源が現在ダミー映像（検証用映像源）状態かどうか（RemoteController が HTTP スレッドから読む。volatile 読み込み）。</summary>
		public bool IsDummyVideo => _dummyVideoActive;

		// 停止時の復元先（直前 URL・UI スレッドのみアクセス）。
		// ダミー開始前に取得するため、冪等ガードによりダミー URL 自身が取得されることはない
		// （防御チェックは StopDummyVideo 側にもある）。
		private string _preDummyVideoUrl = null;

		// 10 分周期で「ダミーが ON のままか」を警告するタイマ（WinForms Timer・UI スレッド）。
		// 非永続トグルの「見落としたまま常時 ON」検知用（_isLogoutBlank と同様、起動毎リセット）。
		// auto-FPS 状態機械（接続＝活動）には接続しない（レンダリング活動判定と無関係なため）。
		private readonly System.Windows.Forms.Timer _dummyVideoWarnTimer;

		private void Browser_IsBrowserInitializedChanged(object sender, EventArgs e)
		{
			if (IsBrowserInitialized)
			{
				// [task20] 保留機構の優先順位（一次レビュー Major 2 対応）:
				// 両方保留されている場合は navigateCache（Navigate）を先に実行し、
				// 続いて _pendingLogoutBlank（LogoutToBlankPage）を実行する。
				// 相互キャンセル（r3 指摘2 対応）:
				//  - LogoutToBlankPage の未初期化保留分岐で navigateCache = null を実行するため、
				//    ここに navigateCache が残存するのは「Logout 保留の後に Navigate が設定」された場合のみ。
				//    その場合も navigateCache を先に実行 → その後 Logout が実行され、結果は空白ページになる。
				//    （ユーザーが最後に望んだ操作は Logout なので、この優先順位はユーザー意図に整合）
				//  - Navigate 側で _pendingLogoutBlank = false を実行するため、
				//    ここに _pendingLogoutBlank が残存するのは「Logout 保留の後に Navigate が
				//    呼び出されなかった」場合のみ（Navigate が呼び出された場合はキャンセル済み）。
				if (navigateCache != null)
				{
					// ロードが完了したので再試行
					string url = navigateCache;            // 非同期コールなのでコピーを取っておく必要がある
					// [task20] フラグクリアは BeginInvoke **前**に統一（一次/二次 Major 3 / r3 指摘5 対応）。
					// 従来は BeginInvoke 後クリアだった。両フラグ（navigateCache / _pendingLogoutBlank）の
					// クリア順序を統一し、BeginInvoke とクリアの間の再評価リスクを排除する
					// （同一UIスレッド上の同期実行のため動作は等価）。
					navigateCache = null;
					BeginInvoke((Action)(() => Navigate(url)));
				}

				// [task20] ログアウト（空白ページ）の保留処理
				// フラグクリアは BeginInvoke **前**に行う（一次レビュー Major 3 / 二次レビュー Major 3 対応）。
				// 多重リクエストや再帰的ディスパッチによる再実行・積み上がりを防止する。
				if (_pendingLogoutBlank)
				{
					_pendingLogoutBlank = false;
					BeginInvoke((Action)(() => LogoutToBlankPage()));
				}
			}
		}

		/// <summary>
		/// 指定した URL のページを開きます。
		/// </summary>
		public void Navigate(string url)
		{
			// [task20] 相互キャンセル（r3 指摘2 対応、変更 D）:
			// 明示的な Navigate が実行された場合、保留中のログアウト（空白ページ）要求をキャンセルする。
			// 「後から実行した方が、相手の保留状態をキャンセルする」原則（設計書 §4.2）による。
			// 呼び出し元はすべて UI スレッド（メニュー / NavigateToLoginPage /
			// Browser_IsBrowserInitializedChanged）のため、_pendingLogoutBlank の書き込みは
			// UI スレッド上で実行されスレッド安全（volatile 修飾により可視性も保証）。
			_pendingLogoutBlank = false;
			// [task22] 明示的なナビゲーションは空白状態を離れる（_pendingLogoutBlank と同一箇所で同一意味論）
			_isLogoutBlank = false;
			
			if (url != Configuration.LogInPageURL || !Configuration.AppliesStyleSheet)
				StyleSheetApplied = false;
			Browser.Load(url);

			if (!IsBrowserInitialized)
			{
				// 大方ロードできないのであとで再試行する
				navigateCache = url;
			}
		}

		// [task20] LogInPageURL の既定値（Configuration.cs の既定値と同一）
		private const string DefaultLogInPageURL = "https://play.games.dmm.com/game/kancolle";

		/// <summary>
		/// [task20] ログインページ URL を取得する。
		/// Configuration が null、または LogInPageURL が null・空文字列の場合は
		/// 既定値にフォールバックする（二次レビュー Major 1 対応: 空文字列のまま
		/// href に埋め込むと about:blank への自己参照リンク（リンク切れ）になるため）。
		/// NavigateToLoginPage とも共通利用し null 防御を統一する（二次レビュー Major 2 対応）。
		/// URL スキーム検証（r3 指摘4 対応）: Uri.TryCreate で http/https スキーム以外
		/// （javascript: 等の不正スキーム・絶対URLとして解析できない値）は既定値へフォールバックする。
		/// </summary>
		private string ResolveLogInPageURL()
		{
			string url = Configuration?.LogInPageURL;
			if (string.IsNullOrEmpty(url))
				return DefaultLogInPageURL;

			// [task20] URL スキーム検証（r3 指摘4 対応）。
			// javascript: 等の不正スキームが設定された場合、そのまま HTML 埋め込みされるのを防ぐため、
			// http/https 以外・解析不能値は既定値にフォールバックする。
			// Uri.TryCreate は .NET Framework 4.8 標準 API（新規パッケージ追加なし）。
			if (Uri.TryCreate(url, UriKind.Absolute, out Uri uriResult) &&
				(uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps))
			{
				return url;
			}
			return DefaultLogInPageURL;
		}

		// [task21] 空白ページ配信用 URL 基底（v1.1: ホスト 127.0.0.1 + 実在しないポート 65535 を明示）。
		// 実在しないポート（65535）を明示することで、意図しないネットワーク接続試行のリスクを排除する
		// （RemoteController は 8080、APIObserver プロキシは 40620 で 65535 とは別）。
		// 不変の URL 文字列のため const（コンパイル時に解決される定数）。
		private const string LogoutBlankUrlBase = "http://127.0.0.1:65535/task20-logout-blank";

		// [task21 v1.1] ts トークンのシーケンスカウンタ（Interlocked.Increment で単調増加・スレッド安全）。
		// プロセス内で単調増加する共有カウンタであるため static フィールド。
		private static long _logoutBlankSeq;

		// [task21 v1.1] ts トークン生成（一次 Minor 1 対応: DateTime.Now.Ticks の衝突可能性を排除）。
		// Ticks（約 100ns 分解能）+ Interlocked.Increment（プロセス内で単調増加・スレッド安全）を結合し、
		// 同一プロセス内で ts 値が重複することを排除する。
		// static フィールドのみを参照しインスタンス状態を参照しないため static メソッド。
		// [task21 v1.3 注記] using System.Threading; を追加すると本ファイル 896 行目の既存
		// System.Windows.Forms.Timer（HeartbeatTimer）参照が System.Threading.Timer とあいまい化
		// （CS0104）するため、設計書 §4.1 注記（v1.3・FS-m1 対応）で許容される完全修飾名
		// System.Threading.Interlocked.Increment を呼び出す方式で実装する（動作・スレッドセーフティ不変）。
		private static string GenerateLogoutBlankToken()
		{
			return string.Format("{0}:{1}", DateTime.Now.Ticks, System.Threading.Interlocked.Increment(ref _logoutBlankSeq));
		}

		/// <summary>
		/// [task20] 本体ブラウザを空白ページへ移動する（2重起動時のログアウト用）。
		/// Cookie は削除しない（DMMセッション維持）。空白ページ上のボタンから
		/// ログインページに戻れば自動ログインとなる。
		/// </summary>
		public void LogoutToBlankPage()
		{
			// UIスレッドで実行する（RemoteController のHTTPスレッド等から呼ばれる可能性に対応）
			if (this.InvokeRequired)
			{
				this.BeginInvoke((Action)(() => LogoutToBlankPage()));
				return;
			}

			// [task20] Browser == null（未生成）時も保留（一次レビュー Critical 2 対応）。
			// 「Browser == null なら return」だとリクエストが完全に失われるため、
			// 未生成・未初期化のいずれも _pendingLogoutBlank で保留し、
			// Browser_IsBrowserInitializedChanged で再実行する。
			if (Browser == null || !Browser.IsBrowserInitialized)
			{
				// [task20] 相互キャンセル（r3 指摘2 対応）:
				// 保留中の Logout は既存の Navigate 要求（navigateCache）をキャンセルする。
				// この分岐で navigateCache をクリアしないと「未初期化時に Logout 保留 →
				// Navigate が navigateCache を設定 → 初期化完了時に両フラグが残存して
				// Navigate が Logout により上書きされる」という TOCTOU 競合が生じる。
				navigateCache = null;
				if (!_pendingLogoutBlank)
				{
					// AddLog(0, ...) は情報ログ相当（r3 指摘7 注記）。
					// 既存パターン（AddLog(1, ...) = 警告）と整合。
					AddLog(0, "[task20] ブラウザ未生成/未初期化のためログアウト（空白ページ）を保留");
				}
				_pendingLogoutBlank = true;
				return;
			}

			// [task20] 実行直前に navigateCache をクリア（一次レビュー Major 2 / r3 指摘2 対応）。
			// 残存する navigateCache による後続 Navigate が空白ページを上書きするのを防ぐ。
			navigateCache = null;

			// StyleSheetApplied = false は UI スレッド上で実行（本メソッドは InvokeRequired チェック済み）。
			// setter は SizeAdjuster.SuspendLayout() 等 UI スレッド制約の操作を行う
			// ため、ここでの代入はスレッド安全（二次レビュー Major 3 注記）。
			StyleSheetApplied = false;

			// [task20] null・空文字列チェック付きでログイン URL を解決（二次レビュー Major 1）
			string loginUrl = ResolveLogInPageURL();

			// [task22] 自ホスト logout.html を優先利用（リモートの /logout-page と同一コンテンツに統一）。
			// ファイル不在時は従来の BuildLogoutBlankHtml インライン生成にフォールバックする（サイレントスルーしない）。
			string pageHtml;
			string logoutHtmlFile = SelfHostedHtml.ReadSelfHostedHtml("logout.html");
			if (logoutHtmlFile != null)
			{
				// main モード: ボタンは data-login-url（HtmlEncode 済みの属性値）へ location.replace
				pageHtml = logoutHtmlFile
					.Replace("__MODE__", "main")
					.Replace("__LOGIN_URL__", System.Net.WebUtility.HtmlEncode(loginUrl));
			}
			else
			{
				AddLog(1, "[task22] logout.html が見つからないためインライン HTML（BuildLogoutBlankHtml）を使用します");
				pageHtml = BuildLogoutBlankHtml(loginUrl);
			}

			// [task21] 修正: about:blank は LoadHtml 第2引数として無効（CefSharp 公式:
			// "other uri's such as about:blank are invalid"）のため、LoadHtml(html, "about:blank")
			// は Uri.TryCreate(Absolute) で失敗し browser.Load が呼ばれず真っ黒になる（task20 項目7 FAIL）。
			// 有効スキーム URL（127.0.0.1:65535、実在しないポート・メモリ配信専用）+
			// CustomResourceRequestHandler 経由のメモリ配信で表示する（task21 §3.4）。
			string blankUrl = string.Format("{0}?ts={1}", LogoutBlankUrlBase, GenerateLogoutBlankToken());
			byte[] blankHtml = Encoding.UTF8.GetBytes(pageHtml);
			// CustomResourceRequestHandler は catch-all（CustomRequestHandler が全リクエストで返す）のため
			// 必ず呼ばれる。配信はメモリ内（実ネットワーク接続なし、§3.4.1）。
			// メモリ解放: RegisterLogoutHtml はエントリ上限（8）超過時に最旧エントリを削除し、
			// Exit() 時に ClearLogoutHtmlStore() で全エントリを解放する（§3.4.2、一次 Major 2 / S-1 対応）。
			CustomResourceRequestHandler.RegisterLogoutHtml(blankUrl, blankHtml);
			Browser.Load(blankUrl);
			// [task22] ログアウト状態フラグを設定（リモートが /login-state で読取）
			_isLogoutBlank = true;
			// AddLog(0, ...) は情報ログ相当（r3 指摘7 注記）。既存パターンと整合。
			// [task21 v1.1] 配信 URL をログに含める（一次 Minor 2: 配信失敗時のデバッグ性向上・可観測性）。
			AddLog(0, string.Format("[task20] ログアウト（空白ページ）を実行: {0}", blankUrl));
		}

		/// <summary>
		/// [task20] ログアウト用の空白ページ HTML を生成する（「ログインページへ」ボタン付き）。
		/// ボタンは location.replace でログインページへ遷移する（履歴には残さない）。
		/// Cookie は削除しない（DMMセッション維持）。
		/// </summary>
		private static string BuildLogoutBlankHtml(string loginUrl)
		{
			// HTML属性（onclick 内 JS 文字列）に埋め込むため HTML エンティティ化（一次レビュー Critical 1 / Major 1 対応）。
			// WebUtility.HtmlEncode は &・<・>・" 等の特殊文字を HTML エンティティに変換する。
			// 旧案の Replace 連打（無意味置換・クォートを空文字に置換して URL を破壊）は廃止。
			// 既定値（https://play.games.dmm.com/game/kancolle）は特殊文字を含まないため、
			// HtmlEncode 後も同一文字列となり、正常時の挙動は不変。
			string safeUrl = System.Net.WebUtility.HtmlEncode(loginUrl);
			// [r3 指摘1 対応] C# 逐語的補間文字列 $@"..." 内では HTML 属性のダブルクォート " は
			// 必ず "" にエスケープする（C# 言語仕様: 逐語文字列内の " は "" でエスケープ）。
			// 単一 " のままだと CS1003 / CS1010 のコンパイルエラーになるため注意。
			// 下方の body 側（<div class=""wrap""> 等）は "" 済み。
			// [r3 指摘6 注記] HTML タグ <br/> はクォート文字を含まないため、
			// C# 逐語文字列内でのエスケープは不要（"" 変換対象ではない）。
			return $@"<!DOCTYPE html>
<html>
<head>
<meta charset=""utf-8"" />
<style>
html,body {{ margin:0; padding:0; width:100%; height:100%; background:#000; color:#fff;
		font-family:'Segoe UI',sans-serif; box-sizing:border-box; }}
.wrap {{ width:100%; height:100%; display:flex; flex-direction:column;
		align-items:center; justify-content:center; gap:24px; }}
.title {{ font-size:28px; letter-spacing:4px; opacity:.85; }}
.btn {{ display:inline-block; padding:14px 40px; font-size:18px; text-decoration:none;
		background:#1e88e5; color:#fff; border-radius:4px; }}
.btn:hover {{ background:#42a5f5; }}
.hint {{ font-size:12px; color:#888; text-align:center; }}
</style>
</head>
<body>
<div class=""wrap"">
		<div class=""title"">LOGGED OUT</div>
		<button type=""button"" class=""btn"" onclick=""location.replace('{safeUrl}')"" >ログインページへ</button>
		<div class=""hint"">Cookieは保持されています。<br/>ログインページに戻れば自動ログインされます。</div>
</div>
</body>
</html>";
		}

		/// <summary>
		/// [task22] 指定 URL がログアウト空白ページ URL（LogoutBlankUrlBase）と一致するかを厳密判定する。
		/// Uri パース後、Host == "127.0.0.1" かつ Port == 65535 かつ AbsolutePath が
		/// "/task20-logout-blank" で始まることを要求する（サブストリング一致の誤検知を排除）。
		/// クエリ（?ts=...）は AbsolutePath に含まれないため影響しない。
		/// </summary>
		private static bool IsLogoutBlankUrl(string url)
		{
			Uri uri;
			if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
				return false;
			return uri.Host == "127.0.0.1"
				&& uri.Port == 65535
				&& uri.AbsolutePath.StartsWith("/task20-logout-blank", StringComparison.Ordinal);
		}

		//------------------------------------------------------------------------------------
		// ダミー映像（検証用映像源）: 開始/停止/状態同期
		// game 未ログイン状態からそのまま起動でき、停止時は直前 URL へ復帰する。
		//------------------------------------------------------------------------------------

		/// <summary>
		/// ダミー映像を開始する（検証映像源）。game 未ログイン状態からそのまま起動可能。
		/// </summary>
		public void StartDummyVideo()
		{
			// HTTP（/control）・メニューのいずれから来ても UI スレッドで実行する
			// （NavigateToLoginPage と同一の InvokeRequired/BeginInvoke パターン）。
			if (this.InvokeRequired)
			{
				this.BeginInvoke((Action)(() => StartDummyVideo()));
				return;
			}

			if (_dummyVideoActive)
				return; // 冪等（重複開始は no-op）

			// 1) 復元先の取得（Navigate 前に読む。冪等ガードによりダミー表示中には本メソッドが
			//    走らないためダミー URL 自身が取得されることはない。null/空は停止時フォールバックで吸収）。
			_preDummyVideoUrl = Browser?.Address;

			// 2) ブラウザがまだ生成されていない場合は開始できない（Browser.Load は NRE となる）。
			//    フラグは立てずエラーログで終了し、状態と画面の乖離を防ぐ。
			if (Browser == null)
			{
				AddLog(2, "[DummyVideo] ブラウザがまだ生成されていないためダミー映像を開始できません");
				return;
			}

			// 3) HTML をメモリ配信ストアへ登録。登録は純メモリ操作で Browser 初期化に依存しないため
			//    ナビゲーション分岐より前に実行する（初期化完了前の起動でも navigateCache 再生時には
			//    ストア登録済みで配信が成立する）。ファイル不在時はインライン最小 HTML で
			//    フォールバックする（サイレントスルーしない）。
			string html = SelfHostedHtml.ReadSelfHostedHtml("dummy_video_test.html");
			if (html == null)
			{
				AddLog(2, "[DummyVideo] dummy_video_test.html が見つからないためインライン最小映像（タイムコード+透かし）を配信します");
				html = BuildDummyVideoFallbackHtml();
			}
			CustomResourceRequestHandler.RegisterDummyVideoHtml(System.Text.Encoding.UTF8.GetBytes(html));

			// 4) ナビゲーション（Navigate 経由: _isLogoutBlank=false の相互取消セマンティクスを踏襲 —
			//    ダミー開始はログアウト保留を取消し、ダミーが最終状態となる）。
			//    初期化完了前でよい（Navigate は URL を navigateCache に保留し、初期化完了時に
			//    再実行されるため、初期化前起動でも初期化完了後にダミー映像が表示される）。
			_dummyVideoActive = true;
			Navigate(CustomResourceRequestHandler.DummyVideoUrlBase);

			// 5) HUD パイプライン検証用のダミー HUD を 1 回注入。静的 JSON のみ（更新タイマは新設しない）。
			//    game なしでは実 HUD が来ないため、この初回注入がそのまま空→非空遷移 broadcast として発火する。
			_remoteController?.UpdateHudData(DummyHudJson);

			// 6) 運用ログ（既定 LogLevel=2 の書き出しフィルタに乗るよう優先度 2。運用者が「本番でダミーが ON に
			//    なっている」ことを eolog で常に追跡できる）。
			AddLog(2, "[DummyVideo] ダミー映像（検証用映像源）を開始しました: " + CustomResourceRequestHandler.DummyVideoUrlBase);

			// 7) 10 分周期警告タイマ開始。
			_dummyVideoWarnTimer.Start();

			SyncDummyVideoMenuChecked();
		}

		/// <summary>ダミー映像を停止し、直前 URL（取得不能/ダミー URL 時はログインページ）へ復帰する。</summary>
		public void StopDummyVideo()
		{
			StopDummyVideo(restore: true);
		}

		// restore=false は「状態のみ解除（ナビゲーションしない）」。手動ナビゲーションによる
		// ダミー離脱（自己治癒）では、ユーザーは既に希望のページにいるため再ナビゲーションしない。
		private void StopDummyVideo(bool restore)
		{
			if (this.InvokeRequired)
			{
				this.BeginInvoke((Action)(() => StopDummyVideo(restore)));
				return;
			}

			if (!_dummyVideoActive)
			{
				// 冪等スキップ経路でもメニューを同期する: 実状態が既に OFFなのに Checked が残留している場合、
				// ここで表示を修復しないと以降の表示と実状態の乖離が残り続ける（表示専用 Checked の全経路同期）
				SyncDummyVideoMenuChecked();
				return; // 冪等（自己治癒の競合による二重解除も安全にスキップ）
			}

			_dummyVideoActive = false;
			CustomResourceRequestHandler.ClearDummyVideoHtml(); // メモリ解放（Exit 経路でも呼ばれるが null 代入のみで冪等）
			_dummyVideoWarnTimer.Stop();
			string restoreUrl = _preDummyVideoUrl;
			_preDummyVideoUrl = null;

			if (restore)
			{
				// ログインページへ復帰が既定。直前 URL が取得できた（null/空でない）場合のみそれへ戻す。
				// ダミー URL 自体（開始直後の異常経路で取得値が汚染された場合）はログインページへ
				// フォールバックし、「停止後もダミーに残る」経路を構造的に排除する。
				if (string.IsNullOrEmpty(restoreUrl) || CustomResourceRequestHandler.IsDummyVideoUrl(restoreUrl))
					restoreUrl = ResolveLogInPageURL();
				Navigate(restoreUrl);
				AddLog(2, "[DummyVideo] ダミー映像を停止しました（復帰: " + restoreUrl + "）");
			}
			else
			{
				AddLog(2, "[DummyVideo] ダミー映像を停止しました（手動ナビゲーションによる自己治癒）");
			}

			SyncDummyVideoMenuChecked();
		}

		// メニュー Checked の同期（UI スレッド上のみ）。Designer 生成前でも呼ばれ得るため null ガード。
		private void SyncDummyVideoMenuChecked()
		{
			if (ToolMenu_Other_DummyVideo != null)
				ToolMenu_Other_DummyVideo.Checked = _dummyVideoActive;
		}

		// HUD パイプライン検証用の静的ダミー HUD。remote_hud.js の updateHudData は
		// 全セクションが任意・ガード付きのため、material のみでも安全にレンダリングされる（未知キーは無視）。
		private const string DummyHudJson =
			"{\"material\":{\"fuel\":999,\"ammo\":888,\"steel\":777,\"bauxite\":666,\"bucket\":55}}";

		// ダミー映像のインラインフォールバック HTML（dummy_video_test.html 不在時）。
		// 自己識別の必須要素（移動図形・HH:MM:SS.mmm タイムコード・回転透かし）のみの最小構成で、
		// rAF ループのみとする（FPS 計測・操作 API は主ファイル側の機能）。
		private static string BuildDummyVideoFallbackHtml()
		{
			return @"<!DOCTYPE html>
<html>
<head>
<meta charset=""utf-8"" />
<title>DUMMY VIDEO (fallback)</title>
<style>html,body{margin:0;padding:0;background:#000;overflow:hidden}</style>
</head>
<body>
<canvas id=""c"" width=""1200"" height=""720""></canvas>
<script>
(function(){
var c=document.getElementById('c'),g=c.getContext('2d'),t0=performance.now(),x=0;
function pad(n,w){var s=''+n;while(s.length<w)s='0'+s;return s;}
function draw(now){
var t=now-t0;
g.fillStyle='#000';g.fillRect(0,0,1200,720);
x=(x+3)%1200;
g.fillStyle='#0f0';g.fillRect(x,330,60,60);
var ms=Math.floor(t%1000),s=Math.floor(t/1000)%60,m=Math.floor(t/60000)%60,h=Math.floor(t/3600000);
g.fillStyle='#fff';g.font='bold 64px monospace';g.textAlign='left';
g.fillText(pad(h,2)+':'+pad(m,2)+':'+pad(s,2)+'.'+pad(ms,3),40,90);
g.save();g.translate(600,360);g.rotate(-Math.PI/9);
g.fillStyle='rgba(255,0,0,0.35)';g.font='bold 140px sans-serif';g.textAlign='center';
g.fillText('DUMMY VIDEO',0,0);g.restore();
requestAnimationFrame(draw);
}
requestAnimationFrame(draw);
})();
</script>
</body>
</html>";
		}

		// ON 操作のみ確認ダイアログ（ゲームページをアンロードするため）。
		// HTTP 経路（StartDummyVideo 直接呼び出し）にはこのガードは存在しない（リモート自動化はサイレント）。
		private void ToolMenu_Other_DummyVideo_Click(object sender, EventArgs e)
		{
			// 分岐は実状態（_dummyVideoActive）基準。Checked は表示専用（SyncDummyVideoMenuChecked のみ更新・
			// CheckOnClick=false でクリック反転なし）であり、ダイアログ中止や外部リセットで実状態と乖離しうるため
			// 分岐情報源にすると「1 回クリックしても効かない」体感遅れの原因となる
			if (_dummyVideoActive)
			{
				StopDummyVideo();
				return;
			}
			var result = MessageBox.Show(
				"ダミー映像（検証用映像源）を開始します。\n現在のページ（ゲームを含む）はアンロードされます。\n出撃・遠征中ではないことを確認してください。",
				"ダミー映像", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
			if (result == DialogResult.Yes)
			{
				StartDummyVideo();
			}
			else
			{
				// 表示専用 Checked と実状態の残留乖離を全経路で修復する（通常は反転発生のため no-op 相当）
				SyncDummyVideoMenuChecked();
			}
		}

		/// <summary>
		/// ブラウザを再読み込みします。
		/// </summary>
		public void RefreshBrowser() => RefreshBrowser(false);

		/// <summary>
		/// ブラウザを再読み込みします。
		/// </summary>
		/// <param name="ignoreCache">キャッシュを無視するか。</param>
		public void RefreshBrowser(bool ignoreCache)
		{
			if (!Configuration?.AppliesStyleSheet ?? false)
				StyleSheetApplied = false;

			// UI スレッドで実行する（WCF 等から来る可能性に対応）
			if (this.InvokeRequired)
			{
				this.BeginInvoke((Action)(() => RefreshBrowser(ignoreCache)));
				return;
			}

			if (Browser == null)
				return;

			try
			{
				// キャッシュ無視モードはフルリロード（Cef の API に合わせる）
				if (ignoreCache)
				{
					Browser?.Reload(true);
					return;
				}

				// 通常はメインフレームだけリロードして副次的な負荷を減らす
				GetMainFrame()?.ExecuteJavaScriptAsync("location.reload();");
				return;
			}
			catch (Exception ex)
			{
				// 応急処置としてログを残しておく
				AddLog(1, "RefreshBrowser failed: " + ex.Message);
				Browser?.Reload(ignoreCache);
			}
		}

		/// <summary>
		/// メインプロセスから送られてきたゲーム内HUDデータを受信し、リモートコントローラーへ中継します。
		/// </summary>
		public void UpdateHudData(string hudJson)
		{
			lock (_hudBufferLock)
			{
				if (_remoteController == null)
				{
					_bufferedHudJson = hudJson;
					return;
				}

				// 1. バッファにある古いデータを先に送信
				if (_bufferedHudJson != null)
				{
					_remoteController.UpdateHudData(_bufferedHudJson);
					_bufferedHudJson = null;
				}

				// 2. 最新データを送信して上書きする（ロック内に含める）
				_remoteController.UpdateHudData(hudJson);
			}
		}

		// フレーム URL ダンプの重複抑止（同一 URL 集合の連続重複は 1 行に留める）。
		// 複数のフレームの FrameLoadEnd が並行して同じハンドラに入り得るため lock で保護する（抑止は best effort）。
		private string _lastFrameUrlDump;
		private readonly object _frameUrlDumpLock = new object();
		// スタイルシート未適用の継続を記録する間隔（ApplyStyleSheet は複数契機で再入するため、同じ状態を毎回記録しない）。
		// DateTime のむきだし読み書きは 32bit 環境で分割読みの可能性があるため ticks(long) を Interlocked で扱う。
		private const long StyleSheetNotAppliedLogIntervalTicks = 30 * TimeSpan.TicksPerSecond;
		private long _lastStyleSheetNotAppliedLogTicks = DateTime.MinValue.Ticks;

		/// <summary>
		/// 全フレームの URL を 1 行へ連結して UI ログへ残す。
		/// ゲーム iframe の URL が既存の判定パターン（ゲームフレーム・キャプチャ源フレーム）と
		/// 一致するかを、再現 1 回で静的に切り替えるための診断証跡であり、動作には影響しない。
		/// 1 ページ遷移で複数フレーム分発火するため、前回と同一の URL 集合は抑止してログスパムにしない。
		/// </summary>
		private void DumpFrameUrls()
		{
			if (!IsBrowserInitialized)
				return;

			var browser = Browser.GetBrowser();
			if (browser == null)
				return;

			var sb = new StringBuilder();
			foreach (var id in browser.GetFrameIdentifiers())
			{
				var frame = browser.GetFrameByIdentifier(id);
				if (frame == null) continue;
				if (sb.Length > 0) sb.Append(" | ");
				sb.Append(string.IsNullOrEmpty(frame.Url) ? "(empty)" : frame.Url);
			}
			if (sb.Length == 0)
				return;

			// 転送は WCF 経由の UI ログのため、異例に長い列（多数の iframe）は先頭部分だけ残して切る
			const int maxLen = 2000;
			string dump = sb.Length > maxLen ? sb.ToString(0, maxLen) + " …(truncated)" : sb.ToString();

			lock (_frameUrlDumpLock)
			{
				if (string.Equals(_lastFrameUrlDump, dump, StringComparison.Ordinal))
					return;
				_lastFrameUrlDump = dump;
			}

			AddLog(0, "[FrameUrls] " + dump);
		}

		/// <summary>
		/// ゲーム iframe が取得できずスタイル未適用が続いている事実を 1 回だけ記録する。
		/// 無音だと「映像源フレームは存在するのにゲーム iframe 判定に不一致でレイアウトが復帰しない」
		/// 状態をフレーム URL ダンプと突き合わせて確定できない（30 秒間隔で間引く）。
		/// </summary>
		private void LogStyleSheetNotApplied()
		{
			// Interlocked は using を追加せず完全修飾で呼ぶ（System.Threading の using 追加は
			// このファイルの Timer（WinForms 側）を曖昧参照させるため）
			long nowTicks = DateTime.UtcNow.Ticks;
			long lastTicks = System.Threading.Interlocked.Read(ref _lastStyleSheetNotAppliedLogTicks);
			if ((nowTicks - lastTicks) < StyleSheetNotAppliedLogIntervalTicks)
				return;
			// 他スレッドと同時に記録した場合は後勝ちになるが、差分は 1 行の増減のみ（正確な間隔保証は不要）
			System.Threading.Interlocked.CompareExchange(ref _lastStyleSheetNotAppliedLogTicks, nowTicks, lastTicks);
			AddLog(1, "[StyleSheet] ゲーム iframe が見つからず、スタイルシート未適用が続いています（レイアウトは未固定のまま）");
		}

		private void Browser_FrameLoadEnd(object sender, FrameLoadEndEventArgs e)
		{
			// キャプチャ源フレーム（艦これゲーム or ダミー映像テストページ）のロード完了時に、常駐キャプチャエンジンを自動注入して起動
			try
			{
				if (IsCaptureSourceFrame(e.Frame))
				{
					e.Frame.ExecuteJavaScriptAsync(CaptureEngineScript);
					// WCS 初期化が「このフレームがまだ無い」理由で降格したまま残っている場合、
					// ここがその状態を閉じる唯一の機会になる（映像源フレームの完了時だけを呼ぶ。
					// 全フレームの load-end で呼ぶと、映像源ではないロードで一度限りの再試行が消費される）
					_remoteController?.NotifyCaptureSourceFrameReady();
				}
			}
			catch (Exception ex)
			{
				SendErrorReport(ex.ToString(), "Browser_FrameLoadEnd: キャプチャエンジンの自動注入に失敗しました。");
			}

			// 全フレーム URL の診断ログ（Q6 判定の証跡・挙動不変）。
			// 下の AppliesStyleSheet（設定値）ガードより前に置くことで、設定が false の環境でも取得できる。
			try
			{
				DumpFrameUrls();
			}
			catch (Exception ex)
			{
				SendErrorReport(ex.ToString(), "Browser_FrameLoadEnd: フレーム URL の診断ログ取得に失敗しました。");
			}

			// Navigate 検証（軽量）: ダミー有効化後にメインフレームのロード完了がダミー URL であることを
			// 確認し、eolog に証跡を残す（読み込み成功の到達確認。追加タイマ・スレッドは新設しない）。
			// 不一致時は下の自己治癒がロールバック＋警告ログを出すため、両条件は排他で二重ログにならない。
			// AddLog は AsyncRemoteRun でメインスレッド側へ配送されるため、本ハンドラから直接呼出して安全
			if (_dummyVideoActive && e.Frame != null && e.Frame.IsMain && CustomResourceRequestHandler.IsDummyVideoUrl(e.Frame.Url))
			{
				AddLog(2, "[DummyVideo] 表示確認: " + e.Frame.Url);
			}

			// 状態自己治癒: ダミー ON のままユーザーが手動でダミー以外のページ（game 等）へ遷移した場合は、
			// 状態フラグ/ストア/警告タイマを UI スレッドで解除する（ナビゲーションはしない — ユーザーは既に希望先にいる）。
			// 解除しないと「映像は game なのに DUMMY バッジ/フラグが ON のまま」になる誤認経路が生まれる。
			// FrameLoadEnd は UI スレッド保証がないため書き込みは BeginInvoke でディスパッチする。
			// volatile 読み取り後に UI スレッド側で StopDummyVideo が先行しても冪等ガードにより二重解除は無害。
			if (_dummyVideoActive && e.Frame != null && e.Frame.IsMain && !CustomResourceRequestHandler.IsDummyVideoUrl(e.Frame.Url))
			{
				if (this.InvokeRequired)
					this.BeginInvoke((Action)(() => StopDummyVideo(restore: false)));
				else
					StopDummyVideo(restore: false);
			}

			// [task22] URL 監視セーフティネット: メインフレーム URL からログアウト状態フラグを更新する。
			// - CefSharp 143.0.90 に AddressChanged イベントが存在しないため、
			//   既存の FrameLoadEnd 内で IFrame.IsMain + IFrame.Url を確認する方式。
			// - 挿入位置: 第 1 try/catch（キャプチャ注入）の後、`if (!Configuration.AppliesStyleSheet) return;`
			//   の直前。この return は Configuration.AppliesStyleSheet（設定値、通常 true）に基づくもので
			//   StyleSheetApplied（インスタンスフィールド）とは別物。
			//   return より前に配置することで、設定値が false の場合にもセーフティネットが機能する。
			try
			{
				if (e.Frame != null && e.Frame.IsMain && e.Frame.Url != null)
				{
					// [task22] 厳密判定（Uri パース）。サブストリング一致（Contains）は
					// "task20-logout-blank-extra" 等の誤検知を防げないため使用しない。
					_isLogoutBlank = IsLogoutBlankUrl(e.Frame.Url);
				}
			}
			catch (Exception ex)
			{
				SendErrorReport(ex.ToString(), "Browser_FrameLoadEnd: ログアウト状態フラグ更新に失敗しました。");
			}
			
			if (!Configuration.AppliesStyleSheet)
				return;

			try
			{
				if (IsMainFrame(e.Frame))
				{
					e.Frame.ExecuteJavaScriptAsync(PageScript);

					// 自己治癒: ゲーム iframe の完了がメインフレームの完了より先に起きると、
					// スタイルシート適用の契機をそのまま失う（その後は無音のまま未適用が続く）。
					// 冪等な既存関数を再適用して閉じる。ゲームフレーム未発見では何もしないため
					// 無ゲーム期の余計な書き込みは発生せず、適用済みはガードで再発火しない。
					if (!StyleSheetApplied)
						BeginInvoke((Action)(ApplyStyleSheet));
				}

				if (IsGameFrame(e.Frame))
				{
					e.Frame.ExecuteJavaScriptAsync(FrameScript);
					StyleSheetApplied = true;
				}
			}
			catch (Exception ex)
			{
				// [task22 一次コードレビュー P-3] 空 catch 修正: サイレントな例外の握りつぶしを禁止
				// （AGENTS.md 制約）するため、同ハンドラの第 1 try/catch と同一パターンで SendErrorReport へ委譲する。
				SendErrorReport(ex.ToString(), "Browser_FrameLoadEnd: PageScript/FrameScript の自動注入に失敗しました。");
			}
		}


		/// <summary>
		/// ズームを適用します。
		/// </summary>
		public void ApplyZoom()
		{
			if (!IsBrowserInitialized)
				return;

			// 最小化や非表示などでサイズが極小になっている場合は処理しない
			// 理由：極小サイズでズーム計算やブラウザサイズの再設定を行うと、Browser.Sizeも極小化され、
			//       リモート操作の座標計算やクリックイベント送信が正常に動作しなくなるため。
			if (SizeAdjuster.Width <= 10 || SizeAdjuster.Height <= 10)
				return;


			double zoomRate = Configuration.ZoomRate;
			bool fit = Configuration.ZoomFit && StyleSheetApplied;


			double zoomFactor;

			if (fit)
			{
				double rateX = (double)SizeAdjuster.Width / KanColleSize.Width;
				double rateY = (double)SizeAdjuster.Height / KanColleSize.Height;
				zoomFactor = Math.Min(rateX, rateY);
			}
			else
			{
				if (zoomRate < 0.1)
					zoomRate = 0.1;
				if (zoomRate > 10)
					zoomRate = 10;

				zoomFactor = zoomRate;
			}


			Browser.SetZoomLevel(Math.Log(zoomFactor, 1.2));


			if (StyleSheetApplied)
			{
				Browser.Size = Browser.MinimumSize = new Size(
					(int)(KanColleSize.Width * zoomFactor),
					(int)(KanColleSize.Height * zoomFactor)
					);
				RememberBrowserSize();

				CenteringBrowser();
			}

			if (fit)
			{
				ToolMenu_Other_Zoom_Current.Text = "現在: ぴったり";
			}
			else
			{
				ToolMenu_Other_Zoom_Current.Text = $"現在: {zoomRate:p1}";
			}

		}



		/// <summary>
		/// スクリーンショットを撮影します。
		/// </summary>
		private async Task<Bitmap> TakeScreenShot()
		{
			var kancolleFrame = GetKanColleFrame();
			if (kancolleFrame == null)
			{
				AddLog(3, string.Format("艦これが読み込まれていないため、スクリーンショットを撮ることはできません。"));
				System.Media.SystemSounds.Beep.Play();
				return null;
			}


			Task<ScreenShotPacket> InternalTakeScreenShot()
			{
				var request = new ScreenShotPacket();

				if (Browser == null || !Browser.IsBrowserInitialized)
					return request.TaskSource.Task;


				string script = $@"
					(async function() {{
						await CefSharp.BindObjectAsync('{request.ID}');

						function waitForCanvas() {{
							return new Promise(resolve => {{
								const check = () => {{
									const canvas = document.querySelector('canvas');
									if (canvas && canvas.width > 0 && canvas.height > 0) {{
										resolve(canvas);
									}} else {{
										requestAnimationFrame(check);
									}}
								}};
								check();
							}});
						}}

						const canvas = await waitForCanvas();

						requestAnimationFrame(() => {{
							try {{
								const dataurl = canvas.toDataURL('image/png');
								{request.ID}.complete(dataurl);
							}} catch (e) {{
								{request.ID}.complete(null);
							}}
						}});
					}})();
					";

				Browser.JavascriptObjectRepository.Register(request.ID, request, true);
				kancolleFrame.ExecuteJavaScriptAsync(script);

				return request.TaskSource.Task;
			}

			var result = await InternalTakeScreenShot();

			// ごみ掃除
			Browser.JavascriptObjectRepository.UnRegister(result.ID);
			kancolleFrame.ExecuteJavaScriptAsync($@"delete {result.ID}");

			return result.GetImage();
		}



		/// <summary>
		/// スクリーンショットを撮影し、設定で指定された保存先に保存します。
		/// </summary>
		public async Task SaveScreenShot()
		{

			int savemode = Configuration.ScreenShotSaveMode;
			int format = Configuration.ScreenShotFormat;
			string folderPath = Configuration.ScreenShotPath;
			bool is32bpp = format != 1 && Configuration.AvoidTwitterDeterioration;

			Bitmap image = null;
			try
			{
				image = await TakeScreenShot();


				if (image == null)
					return;

				if (is32bpp)
				{
					if (image.PixelFormat != PixelFormat.Format32bppArgb)
					{
						var imgalt = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb);
						using (var g = Graphics.FromImage(imgalt))
						{
							g.DrawImage(image, new Rectangle(0, 0, imgalt.Width, imgalt.Height));
						}

						image.Dispose();
						image = imgalt;
					}

					// 不透明ピクセルのみだと jpeg 化されてしまうため、1px だけわずかに透明にする
					Color temp = image.GetPixel(image.Width - 1, image.Height - 1);
					image.SetPixel(image.Width - 1, image.Height - 1, Color.FromArgb(252, temp.R, temp.G, temp.B));
				}
				else
				{
					if (image.PixelFormat != PixelFormat.Format24bppRgb)
					{
						var imgalt = new Bitmap(image.Width, image.Height, PixelFormat.Format24bppRgb);
						using (var g = Graphics.FromImage(imgalt))
						{
							g.DrawImage(image, new Rectangle(0, 0, imgalt.Width, imgalt.Height));
						}

						image.Dispose();
						image = imgalt;
					}
				}


				// to file
				if ((savemode & 1) != 0)
				{
					try
					{
						if (!Directory.Exists(folderPath))
							Directory.CreateDirectory(folderPath);

						string ext;
						ImageFormat imgFormat;

						switch (format)
						{
							case 1:
								ext = "jpg";
								imgFormat = ImageFormat.Jpeg;
								break;
							case 2:
							default:
								ext = "png";
								imgFormat = ImageFormat.Png;
								break;
						}

						string path = $"{folderPath}\\{DateTime.Now:yyyyMMdd_HHmmssff}.{ext}";
						image.Save(path, imgFormat);
						_lastScreenShotPath = path;

						AddLog(2, $"スクリーンショットを {path} に保存しました。");
					}
					catch (Exception ex)
					{
						SendErrorReport(ex.ToString(), "スクリーンショットの保存に失敗しました。");
					}
				}


				// to clipboard
				if ((savemode & 2) != 0)
				{
					try
					{
						Clipboard.SetImage(image);

						if ((savemode & 3) != 3)
							AddLog(2, "スクリーンショットをクリップボードにコピーしました。");
					}
					catch (Exception ex)
					{
						SendErrorReport(ex.ToString(), "スクリーンショットのクリップボードへのコピーに失敗しました。");
					}
				}

			}
			catch (Exception ex)
			{
				SendErrorReport(ex.ToString(), "スクリーンショットの撮影に失敗しました。");
			}
			finally
			{
				image?.Dispose();
			}

		}


		public void SetProxy(string proxy)
		{
			ushort port;
			if (ushort.TryParse(proxy, out port))
			{
				ProxySettings = "127.0.0.1:" + port;
			}
			else if (!string.IsNullOrEmpty(proxy))
			{
				var match = System.Text.RegularExpressions.Regex.Match(proxy, @":(\d+)");
				if (match.Success && ushort.TryParse(match.Groups[1].Value, out port))
				{
					ProxySettings = "127.0.0.1:" + port;
				}
				else
				{
					ProxySettings = proxy;
				}
			}
			else
			{
				ProxySettings = proxy;
			}

			if (this.InvokeRequired)
			{
				this.Invoke((Action)(() => InitializeBrowser()));
			}
			else
			{
				InitializeBrowser();
			}

			BrowserHost.AsyncRemoteRun(() => BrowserHost.Proxy.SetProxyCompleted());
		}



		public void SetIconResource(byte[] canvas)
		{
			try
			{
				string[] keys = new string[] {
					"Browser_ScreenShot",
					"Browser_Zoom",
					"Browser_ZoomIn",
					"Browser_ZoomOut",
					"Browser_Unmute",
					"Browser_Mute",
					"Browser_Refresh",
					"Browser_Navigate",
					"Browser_Other",
				};
				int unitsize = 16 * 16 * 4;

				// 画像リストの重複・不整合を防ぐため一度クリア
				Icons.Images.Clear();

				bool isDataValid = false;
				if (canvas != null && canvas.Length >= unitsize * keys.Length)
				{
					// 先頭一部バイトの非0チェックで空データを検知
					for (int i = 0; i < Math.Min(canvas.Length, 100); i++)
					{
						if (canvas[i] != 0) { isDataValid = true; break; }
					}
				}

				if (!isDataValid)
				{
					AddLog(3, "[FormBrowser] SetIconResource: 警告 - 受信したアイコンデータが null または全ピクセル透明 (ARGB=0) です。");
				}
				else
				{
					AddLog(2, $"[FormBrowser] SetIconResource: 有効なアイコンデータ ({canvas.Length} bytes) を正常に受領しました。");
				}

				for (int i = 0; i < keys.Length; i++)
				{
					Bitmap bmp = new Bitmap(16, 16, PixelFormat.Format32bppArgb);

					if (canvas != null && canvas.Length >= unitsize * (i + 1))
					{
						BitmapData bmpdata = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
						Marshal.Copy(canvas, unitsize * i, bmpdata.Scan0, unitsize);
						bmp.UnlockBits(bmpdata);
					}

					Icons.Images.Add(keys[i], bmp);
				}

				ToolMenu_ScreenShot.Image = ToolMenu_Other_ScreenShot.Image =
					Icons.Images["Browser_ScreenShot"];
				ToolMenu_Zoom.Image = ToolMenu_Other_Zoom.Image =
					Icons.Images["Browser_Zoom"];
				ToolMenu_Other_Zoom_Increment.Image =
					Icons.Images["Browser_ZoomIn"];
				ToolMenu_Other_Zoom_Decrement.Image =
					Icons.Images["Browser_ZoomOut"];
				ToolMenu_Refresh.Image = ToolMenu_Other_Refresh.Image =
					Icons.Images["Browser_Refresh"];
				ToolMenu_NavigateToLogInPage.Image = ToolMenu_Other_NavigateToLogInPage.Image =
					Icons.Images["Browser_Navigate"];
				ToolMenu_Other.Image =
					Icons.Images["Browser_Other"];

				ToolMenu_ColorFilter.Image = Icons.Images["Browser_ScreenShot"]; // TODO: 専用アイコンが無い為、とりあえずスクリーンショットと同じアイコン
				ToolMenu_ColorFilter.Text = "プライバシーフィルタの切り替え";

				SetVolumeState();
			}
			catch (Exception ex)
			{
				AddLog(3, $"[FormBrowser] SetIconResource エラー: {ex.Message}");
			}
		}


		private void TryGetVolumeManager()
		{
			_volumeManager = VolumeManager.CreateInstanceByProcessName("CefSharp.BrowserSubprocess");
		}

		private void SetVolumeState()
		{

			bool mute;
			float volume;

			try
			{
				if (_volumeManager == null)
				{
					TryGetVolumeManager();
				}

				mute = _volumeManager.IsMute;
				volume = _volumeManager.Volume * 100;

			}
			catch (Exception)
			{
				// 音量データ取得不能時
				_volumeManager = null;
				mute = false;
				volume = 100;
			}

			ToolMenu_Mute.Image = ToolMenu_Other_Mute.Image =
				Icons.Images[mute ? "Browser_Mute" : "Browser_Unmute"];

			{
				var control = ToolMenu_Other_Volume_VolumeControl;
				control.Tag = false;
				control.Value = (decimal)volume;
				control.Tag = true;
			}

			Configuration.Volume = volume;
			Configuration.IsMute = mute;
			ConfigurationUpdated();
		}


		private async void ToolMenu_Other_ScreenShot_Click(object sender, EventArgs e)
		{
			await SaveScreenShot();
		}

		private void ToolMenu_Other_Zoom_Decrement_Click(object sender, EventArgs e)
		{
			Configuration.ZoomRate = Math.Max(Configuration.ZoomRate - 0.2, 0.1);
			Configuration.ZoomFit = ToolMenu_Other_Zoom_Fit.Checked = false;
			ApplyZoom();
			ConfigurationUpdated();
		}

		private void ToolMenu_Other_Zoom_Increment_Click(object sender, EventArgs e)
		{
			Configuration.ZoomRate = Math.Min(Configuration.ZoomRate + 0.2, 10);
			Configuration.ZoomFit = ToolMenu_Other_Zoom_Fit.Checked = false;
			ApplyZoom();
			ConfigurationUpdated();
		}

		private void ToolMenu_Other_Zoom_Click(object sender, EventArgs e)
		{

			double zoom;

			if (sender == ToolMenu_Other_Zoom_25)
				zoom = 0.25;
			else if (sender == ToolMenu_Other_Zoom_50)
				zoom = 0.50;
			else if (sender == ToolMenu_Other_Zoom_Classic)
				zoom = 0.667;       // 2/3 ジャストだと 799x479 になる
			else if (sender == ToolMenu_Other_Zoom_75)
				zoom = 0.75;
			else if (sender == ToolMenu_Other_Zoom_100)
				zoom = 1;
			else if (sender == ToolMenu_Other_Zoom_150)
				zoom = 1.5;
			else if (sender == ToolMenu_Other_Zoom_200)
				zoom = 2;
			else if (sender == ToolMenu_Other_Zoom_250)
				zoom = 2.5;
			else if (sender == ToolMenu_Other_Zoom_300)
				zoom = 3;
			else if (sender == ToolMenu_Other_Zoom_400)
				zoom = 4;
			else
				zoom = 1;

			Configuration.ZoomRate = zoom;
			Configuration.ZoomFit = ToolMenu_Other_Zoom_Fit.Checked = false;
			ApplyZoom();
			ConfigurationUpdated();
		}

		private void ToolMenu_Other_Zoom_Fit_Click(object sender, EventArgs e)
		{
			Configuration.ZoomFit = ToolMenu_Other_Zoom_Fit.Checked;
			ApplyZoom();
			ConfigurationUpdated();
		}


		//ズームUIの使いまわし
		private void ToolMenu_Other_DropDownOpening(object sender, EventArgs e)
		{
			var list = ToolMenu_Zoom.DropDownItems.Cast<ToolStripItem>().ToArray();
			ToolMenu_Other_Zoom.DropDownItems.AddRange(list);
		}

		private void ToolMenu_Zoom_DropDownOpening(object sender, EventArgs e)
		{

			var list = ToolMenu_Other_Zoom.DropDownItems.Cast<ToolStripItem>().ToArray();
			ToolMenu_Zoom.DropDownItems.AddRange(list);
		}


		private void ToolMenu_Other_Mute_Click(object sender, EventArgs e)
		{
			if (_volumeManager == null)
			{
				TryGetVolumeManager();
			}

			try
			{
				_volumeManager.ToggleMute();

			}
			catch (Exception)
			{
				System.Media.SystemSounds.Beep.Play();
			}

			SetVolumeState();
		}

		void ToolMenu_Other_Volume_ValueChanged(object sender, EventArgs e)
		{

			var control = ToolMenu_Other_Volume_VolumeControl;

			if (_volumeManager == null)
			{
				TryGetVolumeManager();
			}

			try
			{
				if ((bool)control.Tag)
					_volumeManager.Volume = (float)(control.Value / 100);
				control.BackColor = SystemColors.Window;

			}
			catch (Exception)
			{
				control.BackColor = Color.MistyRose;

			}

		}


		private void ToolMenu_Other_Refresh_Click(object sender, EventArgs e)
		{

			if (!Configuration.ConfirmAtRefresh ||
				MessageBox.Show("再読み込みします。\r\nよろしいですか？", "確認",
				MessageBoxButtons.OKCancel, MessageBoxIcon.Exclamation, MessageBoxDefaultButton.Button2)
				== DialogResult.OK)
			{
				RefreshBrowser();
			}
		}

		private void ToolMenu_Other_RefreshIgnoreCache_Click(object sender, EventArgs e)
		{
			if (!Configuration.ConfirmAtRefresh ||
				MessageBox.Show("キャッシュを無視して再読み込みします。\r\nよろしいですか？", "確認",
				MessageBoxButtons.OKCancel, MessageBoxIcon.Exclamation, MessageBoxDefaultButton.Button2)
				== DialogResult.OK)
			{
				RefreshBrowser(true);
			}
		}

		private void ToolMenu_Other_NavigateToLogInPage_Click(object sender, EventArgs e)
		{

			if (MessageBox.Show("ログインページへ移動します。\r\nよろしいですか？", "確認",
				MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2)
				== DialogResult.OK)
			{

				Navigate(Configuration.LogInPageURL);
			}

		}

		private void ToolMenu_Other_Navigate_Click(object sender, EventArgs e)
		{
			BrowserHost.AsyncRemoteRun(() => BrowserHost.Proxy.RequestNavigation(Browser.GetMainFrame()?.Url ?? ""));
		}

		private void ToolMenu_Other_AppliesStyleSheet_Click(object sender, EventArgs e)
		{
			Configuration.AppliesStyleSheet = ToolMenu_Other_AppliesStyleSheet.Checked;
			if (!Configuration.AppliesStyleSheet)
				RestoreStyleSheet = true;
			ApplyStyleSheet();
			ApplyZoom();
			ConfigurationUpdated();
		}

		private void ToolMenu_Other_Alignment_Click(object sender, EventArgs e)
		{

			if (sender == ToolMenu_Other_Alignment_Top)
				ToolMenu.Dock = DockStyle.Top;
			else if (sender == ToolMenu_Other_Alignment_Bottom)
				ToolMenu.Dock = DockStyle.Bottom;
			else if (sender == ToolMenu_Other_Alignment_Left)
				ToolMenu.Dock = DockStyle.Left;
			else
				ToolMenu.Dock = DockStyle.Right;

			Configuration.ToolMenuDockStyle = (int)ToolMenu.Dock;

			ConfigurationUpdated();
		}

		private void ToolMenu_Other_Alignment_Invisible_Click(object sender, EventArgs e)
		{
			ToolMenu.Visible =
			Configuration.IsToolMenuVisible = false;
			ConfigurationUpdated();
		}



		private void SizeAdjuster_DoubleClick(object sender, EventArgs e)
		{
			ToolMenu.Visible =
			Configuration.IsToolMenuVisible = true;
			ConfigurationUpdated();
		}

		private void ContextMenuTool_ShowToolMenu_Click(object sender, EventArgs e)
		{
			ToolMenu.Visible =
			Configuration.IsToolMenuVisible = true;
			ConfigurationUpdated();
		}

		private void ContextMenuTool_Opening(object sender, CancelEventArgs e)
		{

			if (IsKanColleLoaded || ToolMenu.Visible)
				e.Cancel = true;
		}


		private void ToolMenu_ScreenShot_Click(object sender, EventArgs e)
		{
			ToolMenu_Other_ScreenShot_Click(sender, e);
		}

		private void ToolMenu_Mute_Click(object sender, EventArgs e)
		{
			ToolMenu_Other_Mute_Click(sender, e);
		}

		private void ToolMenu_Refresh_Click(object sender, EventArgs e)
		{
			ToolMenu_Other_Refresh_Click(sender, e);
		}

		private void ToolMenu_NavigateToLogInPage_Click(object sender, EventArgs e)
		{
			ToolMenu_Other_NavigateToLogInPage_Click(sender, e);
		}




		private void FormBrowser_Activated(object sender, EventArgs e)
		{
			if (Browser == null)
				return;

			Browser.Focus();
		}

		private void ToolMenu_Other_Alignment_DropDownOpening(object sender, EventArgs e)
		{

			foreach (var item in ToolMenu_Other_Alignment.DropDownItems)
			{
				var menu = item as ToolStripMenuItem;
				if (menu != null)
				{
					menu.Checked = false;
				}
			}

			switch ((DockStyle)Configuration.ToolMenuDockStyle)
			{
				case DockStyle.Top:
					ToolMenu_Other_Alignment_Top.Checked = true;
					break;
				case DockStyle.Bottom:
					ToolMenu_Other_Alignment_Bottom.Checked = true;
					break;
				case DockStyle.Left:
					ToolMenu_Other_Alignment_Left.Checked = true;
					break;
				case DockStyle.Right:
					ToolMenu_Other_Alignment_Right.Checked = true;
					break;
			}

			ToolMenu_Other_Alignment_Invisible.Checked = !Configuration.IsToolMenuVisible;
		}


		private void ToolMenu_Other_LastScreenShot_DropDownOpening(object sender, EventArgs e)
		{

			try
			{

				using (var fs = new FileStream(_lastScreenShotPath, FileMode.Open, FileAccess.Read))
				{
					ToolMenu_Other_LastScreenShot_Control.Image?.Dispose();

					ToolMenu_Other_LastScreenShot_Control.Image = Image.FromStream(fs);
				}

			}
			catch (Exception)
			{
				// *ぷちっ*
			}

		}

		void ToolMenu_Other_LastScreenShot_ImageHost_Click(object sender, EventArgs e)
		{
			if (_lastScreenShotPath != null && File.Exists(_lastScreenShotPath))
				Process.Start(_lastScreenShotPath);
		}

		private void ToolMenu_Other_LastScreenShot_OpenScreenShotFolder_Click(object sender, EventArgs e)
		{
			if (Directory.Exists(Configuration.ScreenShotPath))
				Process.Start(Configuration.ScreenShotPath);
		}

		private void ToolMenu_Other_LastScreenShot_CopyToClipboard_Click(object sender, EventArgs e)
		{

			if (_lastScreenShotPath != null && File.Exists(_lastScreenShotPath))
			{
				try
				{
					using (var img = new Bitmap(_lastScreenShotPath))
					{
						Clipboard.SetImage(img);
						AddLog(2, string.Format("スクリーンショット {0} をクリップボードにコピーしました。", _lastScreenShotPath));
					}
				}
				catch (Exception ex)
				{
					SendErrorReport(ex.Message, "スクリーンショットのクリップボードへのコピーに失敗しました。");
				}
			}
		}

		private void ToolMenu_Other_OpenDevTool_Click(object sender, EventArgs e)
		{
			if (!IsBrowserInitialized)
				return;

			Browser.GetBrowser().ShowDevTools();
		}

		private void ToolMenu_Other_ClearCache_Click(object sender, EventArgs e)
		{
			if (MessageBox.Show("キャッシュをクリアするため、ブラウザを再起動します。\r\nよろしいですか？\r\n※環境によっては本ツールが終了する場合があります。その場合は再起動してください。", "ブラウザ再起動確認",
				MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
			{
				BrowserHost.AsyncRemoteRun(() => BrowserHost.Proxy.ClearCache());
			}
		}

		protected override void WndProc(ref Message m)
		{

			if (m.Msg == WM_ERASEBKGND)
				// ignore this message
				return;

			// セッション接続/切断通知を RemoteController の表示状態監視へ転送
			// （WTSRegisterSessionNotification の購読はこのウィンドウハンドルで行い、ここで受けて回送する）
			if (m.Msg == WM_WTSSESSION_CHANGE)
				_remoteController?.HandleSessionNotification(m.WParam.ToInt32());

			base.WndProc(ref m);
		}


		#region 呪文


		[DllImport("user32.dll", EntryPoint = "GetWindowLongA", SetLastError = true)]
		private static extern uint GetWindowLong(IntPtr hwnd, int nIndex);

		[DllImport("user32.dll", EntryPoint = "SetWindowLongA", SetLastError = true)]
		private static extern uint SetWindowLong(IntPtr hwnd, int nIndex, uint dwNewLong);

		private const int GWL_STYLE = (-16);
		private const uint WS_CHILD = 0x40000000;
		private const uint WS_VISIBLE = 0x10000000;
		private const int WM_ERASEBKGND = 0x14;

		/// <summary>セッション接続/切断通知（wtsapi32）。RemoteController の表示状態監視へ転送する</summary>
		private const int WM_WTSSESSION_CHANGE = 0x02B1;





		#endregion
	}



	/// <summary>
	/// ウィンドウが非アクティブ状態から1回のクリックでボタンが押せる ToolStrip です。
	/// </summary>
	internal class ExtraToolStrip : ToolStrip
	{
		public ExtraToolStrip() : base() { }

		private const uint WM_MOUSEACTIVATE = 0x21;
		private const uint MA_ACTIVATE = 1;
		private const uint MA_ACTIVATEANDEAT = 2;
		private const uint MA_NOACTIVATE = 3;
		private const uint MA_NOACTIVATEANDEAT = 4;

		protected override void WndProc(ref Message m)
		{
			base.WndProc(ref m);

			if (m.Msg == WM_MOUSEACTIVATE && m.Result == (IntPtr)MA_ACTIVATEANDEAT)
				m.Result = (IntPtr)MA_ACTIVATE;
		}
	}

}
