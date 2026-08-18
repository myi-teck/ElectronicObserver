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
					isEncoding: false,     // 排他フラグ
	
					// WebCodecs 状態管理（設計書 ver 3.9 §5.1.1 対応）
					videoEncoder: null,
					isReinitializing: false,
					encoderConfig: {
						codec: 'avc1.42001f',
						width: 1200,
						height: 720,
						bitrate: 800000,
						framerate: 30,
						avc: { format: 'avc' }
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

						// WebGL バッファ保存 ＋ バックグラウンド WebP キャッシュ生成
						const loop = () => {
							try {
								if (canvas.width > 0 && !this.isEncoding) {
									if (offCanvas.width !== canvas.width || offCanvas.height !== canvas.height) {
										offCanvas.width = canvas.width;
										offCanvas.height = canvas.height;
									}
									offCtx.drawImage(canvas, 0, 0);

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
											}
											this.isEncoding = false;
										};
										reader.onerror = () => {
											this.isEncoding = false;
										};
										reader.readAsDataURL(blob);
									}, 'image/webp', this.webpQuality);
								}
							} catch (e) {
								this.isEncoding = false;
							}
							requestAnimationFrame(loop);
						};
						requestAnimationFrame(loop);
					},

					// C# から EvaluateScriptAsync で呼び出され、事前に完成している WebP キャッシュを即座(0.5ms)に渡す
					pull: function(force) {
						if (this.lastWebp) {
							return ['full', this.lastWebp];
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
								return ['full', dataUrl];
							} catch (e) {
								console.error('[plan02-1-4] toDataURLフォールバック失敗:', e.message);
							}
						}
						return ['none', ''];
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

							// コーデックサポート確認
							var supported = await VideoEncoder.isConfigSupported({
								codec: config.codec,
								width: config.width,
								height: config.height,
								bitrate: config.bitrate
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
								// 【レビュー修正】 WebGL Canvas の場合、ブラウザ実装依存の制限がある
								// Chromium 143 では new VideoFrame(canvas, { alpha: 'discard' }) は正常動作確認済み
								// ただし、一部のブラウザ実装では WebGL 内部テクスチャのコピーが正しく行われない可能性あり
								// 代替案: canvas.captureStream() を経由する方法も検討可能
								// 【Critical修正】 duration を明示的に指定（設計書 §5.1.1 整合性）
								// duration 省略時はブラウザ実装依存でフレームレートが不整合になり、
								// キーフレーム間隔計算誤差が発生する
								// [task19_v1 Critical#4 修正] VideoFrameInit に timestamp（μs・単調増加）を追加
								// W3C WebCodecs 仕様に従い、canvas ソースからの VideoFrame 生成時は
								// VideoFrameInit.timestamp が必須（Chromium は欠如時に
								// VideoFrameInit must provide timestamp で全フレームを拒否する）。
								// FPS 変動で非単調になり得る performance.now() の単純使用を避け、
								// 独立カウンタ（_frameTimestampCounter）で単調増加を保証する（設計書 §4.2.1 整合）。
								// カウンタは this に保持し、initEncoder() でリセットしないため、
								// reinit（再初期化）後も単調増加が維持される。
								var fps = (this.encoderConfig && this.encoderConfig.framerate) || 30;
								if (!this._frameTimestampCounter) this._frameTimestampCounter = 0;
								this._frameTimestampCounter += Math.floor(1000000 / fps);
								var frameTimestamp = this._frameTimestampCounter;
								frame = new VideoFrame(canvas, {
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
							var bytes = new Uint8Array(entry.data);
							var chunkSize = BASE64_ENCODE_CHUNK_SIZE;  // V8 apply 引数制限の安全域 8192
							var chunks = [];
							for (var i = 0; i < bytes.length; i += chunkSize) {
								var end = Math.min(i + chunkSize, bytes.length);
								chunks.push(String.fromCharCode.apply(null, bytes.subarray(i, end)));
							}
							var binary = chunks.join('');  // O(n) 連結（O(n²) の binary += を回避）
							var base64 = btoa(binary);
	
							// [task19 P2] SPS/PPS (description) を第4要素として返却（設計書 §4.2.2 ステップ1-2）
							// description がある場合のみ Base64 文字列、なければ null
							// C# 側が Convert.ToBase64String() 相当で復号し、Type=0x02 フレームでクライアントへ送信する
							var descriptionBase64 = null;
							if (entry.description && entry.description.length > 0) {
								var descBytes = entry.description;
								var descChunks = [];
								for (var j = 0; j < descBytes.length; j += chunkSize) {
									var descEnd = Math.min(j + chunkSize, descBytes.length);
									descChunks.push(String.fromCharCode.apply(null, descBytes.subarray(j, descEnd)));
								}
								descriptionBase64 = btoa(descChunks.join(''));
							}
	
							return ['encoded', base64, entry.isKeyFrame ? '1' : '0', descriptionBase64];
						} catch (e) {
							console.error('[CaptureEngine] pull_encoded error:', e);
							return ['none', null];
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

		private BrowserLib.BrowserConfiguration Configuration;

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


		private bool _styleSheetApplied;
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
						Browser.Size = SizeAdjuster.Size;
					}
					SizeAdjuster.ResumeLayout();
				}

				_styleSheetApplied = value;
			}
		}

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


		void Exit()
		{
			if (!BrowserHost.Closed)
			{
				BrowserHost.Close();
				HeartbeatTimer.Stop();
				_remoteController?.Dispose();
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
			Navigate(Configuration.LogInPageURL);
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
			IsKanColleLoaded = true;

			//ロード直後の適用ではレイアウトがなぜか崩れるのでこのタイミングでも適用
			ApplyStyleSheet();
			ApplyZoom();
			ApplyColorFilter();
			DestroyDMMreloadDialog();

			//起動直後はまだ音声が鳴っていないのでミュートできないため、この時点で有効化
			SetVolumeState();
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
				}
				return;
			}

			ApplyZoom();
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
		private void Browser_IsBrowserInitializedChanged(object sender, EventArgs e)
		{
			if (IsBrowserInitialized && navigateCache != null)
			{
				// ロードが完了したので再試行
				string url = navigateCache;            // 非同期コールするのでコピーを取っておく必要がある
				BeginInvoke((Action)(() => Navigate(url)));
				navigateCache = null;
			}
		}

		/// <summary>
		/// 指定した URL のページを開きます。
		/// </summary>
		public void Navigate(string url)
		{
			if (url != Configuration.LogInPageURL || !Configuration.AppliesStyleSheet)
				StyleSheetApplied = false;
			Browser.Load(url);

			if (!IsBrowserInitialized)
			{
				// 大方ロードできないのであとで再試行する
				navigateCache = url;
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

		private void Browser_FrameLoadEnd(object sender, FrameLoadEndEventArgs e)
		{
			// 艦これゲームフレームのロード完了時に、常駐キャプチャエンジンを自動注入して起動
			// 2026/06/01 myi 新規作成
			try
			{
				if (IsKanColleFrame(e.Frame))
				{
					e.Frame.ExecuteJavaScriptAsync(CaptureEngineScript);
				}
			}
			catch (Exception ex)
			{
				SendErrorReport(ex.ToString(), "Browser_FrameLoadEnd: キャプチャエンジンの自動注入に失敗しました。");
			}

			if (!Configuration.AppliesStyleSheet)
				return;

			try
			{
				if (IsMainFrame(e.Frame))
				{
					e.Frame.ExecuteJavaScriptAsync(PageScript);
				}

				if (IsGameFrame(e.Frame))
				{
					e.Frame.ExecuteJavaScriptAsync(FrameScript);
					StyleSheetApplied = true;
				}
			}
			catch { }
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
