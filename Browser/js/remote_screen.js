//=============================================================================================
// remote_screen.js
// Canvas差分WebP描画・拡大鏡・画面フィルター
// 依存: remote_config.js, remote_connection.js
//=============================================================================================

// DOM要素の取得（ページ読み込み時に実行される）
const screen = document.getElementById('game-screen');

// [task19 v5] Critical 4 修正: screen 変数が宣言されている remote_screen.js 側で rect キャッシュを定義・公開
// script 読み込み順: remote_config.js → remote_connection.js → remote_screen.js → remote_input.js → remote_hud.js → remote_main.js
// （RemoteController.html の実読み込み順。remote_connection.js が remote_screen.js より先読み込み）
// remote_screen.js が screen 変数を所有し、remote_input.js が getCachedGameScreenRect() を呼び出す
// [task27 用語注] `screen` = remote_screen.js:8 の `const screen = document.getElementById('game-screen')`（canvas 要素ローカル変数）。
// `window.screen`（画面解像度 API）とは無関係。canvas 内在サイズ `screen.width × screen.height` は
// RemoteController.html:33 の HTML attribute（1200×720）で固定・JS での再代入なし
// [task27] キャッシュ内容の変更: canvas の box rect（letterbox 込み）→ content rect（letterbox 適用後）
//   - object-fit: contain の letterbox を canvas box（W×H）と canvas 内在（screen.width×screen.height = 1200×720 = 5:3）から復元
//     scale = min(W/iw, H/ih) / content = iw*scale × ih*scale / offset = (W−contentW)/2, (H−contentH)/2
//   - box が 5:3 ちょうど（top/bottom/縦画面）では content = box（offset 0）で現行挙動と等価
//   - box rect は getCachedGameScreenBoxRect() で引き続き公開（仮想カーソルの container ローカル変換用）
let _cachedBoxRect = null;
let _cachedContentRect = null;
let _gameScreenRectCacheValid = false;

// [task27] object-fit: contain サポート判定（極古ブラウザのフォールバック: content = box = 現行挙動）
// [M-1/E-1 反映] `CSS.supports` 自体が非存在する極古環境では「サポート不明」を安全側 = false とみなす。
// object-fit 非サポート環境では letterbox 自体が存在しない（canvas が box に引き伸ばされる）ため content = box が正しく、
// false 側（content = box フォールバック）に落ちる方が安全。対象環境（CefSharp Chromium 91+ / PWA iOS Safari 14+）
// では CSS.supports は常に存在するため挙動は変わらない
const _supportsObjectFitContain =
    (typeof CSS !== 'undefined' && typeof CSS.supports === 'function')
        ? CSS.supports('object-fit', 'contain')
        : false;

// [task27] box rect から content rect（object-fit: contain 相当）を算出
// canvas 内在サイズ（RemoteController.html:33 で 1200×720 固定・JS での再代入なし）を
// contain 計算の一次情報源とする（CSS 変数 --game-aspect-ratio と値は一致する 5:3）
function computeContentRect(boxRect) {
    // [v1.2・Suggestion 1 (r1 critical-review) 反映] boxRect が null / undefined の防御的ガードを関数先頭に明示。
    // getBoundingClientRect() は通常 null を返さないが、万一 falsy な場合は関数先頭でガードしゼロ rect を返す。
    // 呼び出し側の既存ガード（rect.width === 0 / rect.height === 0・task17）がそのまま処理をスキップさせる。
    if (!boxRect || boxRect.width <= 0 || boxRect.height <= 0) {
        // ゼロサイズ（要素非表示等）または boxRect 非存在: ゼロ rect を返し、呼び出し側の既存ガード（rect.width === 0 / rect.height === 0）で処理をスキップさせる
        return { left: 0, top: 0, width: 0, height: 0 };
    }
    const boxW = boxRect.width;
    const boxH = boxRect.height;
    const intrinsicW = screen.width;
    const intrinsicH = screen.height;
    if (!intrinsicW || !intrinsicH || !_supportsObjectFitContain) {
        // 内在サイズ未設定 / object-fit 未サポート（極古ブラウザ・canvas が box に引き伸ばされる）:
        // letterbox は生じないため box = content（現行挙動と等価）
        // [M-3 反映] ゼロサイズ判定の優先順位: 上段の box ゼロサイズガード（boxW/boxH <= 0 → ゼロ rect）が先に効くため、
        // ここに到達し得るのは「box 非ゼロ + 内在サイズ 0」のみ。canvas 内在サイズは HTML attribute（1200×720）で固定・
        // JS 再代入なしのため 0 になることは想定外であり、本フォールバックは防御的（挙動: content = box）
        return { left: boxRect.left, top: boxRect.top, width: boxW, height: boxH };
    }
    const scale = Math.min(boxW / intrinsicW, boxH / intrinsicH);
    const contentW = intrinsicW * scale;
    const contentH = intrinsicH * scale;
    return {
        left: boxRect.left + (boxW - contentW) / 2,
        top: boxRect.top + (boxH - contentH) / 2,
        width: contentW,
        height: contentH
    };
}

function _refreshGameScreenRects() {
    _cachedBoxRect = screen.getBoundingClientRect();
    _cachedContentRect = computeContentRect(_cachedBoxRect);
    _gameScreenRectCacheValid = true;
}

// #game-screen の **content rect（letterbox 適用後）** を返す。
// [task27] セマンティクス変更: 修正前は box rect（letterbox 込み）を返していた。
// 呼び出し側（座標正規化・仮想カーソル・拡大鏡）は全て content 基準を前提にする。
function getCachedGameScreenRect() {
    if (!_gameScreenRectCacheValid || _cachedBoxRect === null) {
        _refreshGameScreenRects();
    }
    return _cachedContentRect;
}

// [task27] #game-screen の **box rect（letterbox 込み）** を返す。
// 仮想カーソルを #screen-container（= canvas box）のローカル座標へ変換する際に使用。
// [task27・S-1 反映] 設計意図（設計書 §3.7）: 座標系（vCursor / 正規化 / 表示 / 送信）は content 基準を統一しており、
// box は以下の 2 用途にのみ使用する:
//   1. letterbox オフセット算出（updateCursorDisplay() 内の (rect.left - boxRect.left) / (rect.top - boxRect.top) 項）
//   2. 仮想カーソルの container ローカル変換（updateCursorDisplay() のみ・唯一の呼び出し箇所）
// 座標の正規化には box を使用しないこと（タッチ / マウス / 拡大鏡は全て content 基準 = getCachedGameScreenRect()）。
function getCachedGameScreenBoxRect() {
    if (!_gameScreenRectCacheValid || _cachedBoxRect === null) {
        _refreshGameScreenRects();
    }
    return _cachedBoxRect;
}

// remote_input.js および他のモジュールから参照可能に window に公開
window.getCachedGameScreenRect = getCachedGameScreenRect;
window.getCachedGameScreenBoxRect = getCachedGameScreenBoxRect;

// [task19 不具合2修正] レイアウト変更時に rect キャッシュを無効化する公開関数
// resize イベントだけでなく、HUD バー表示/非表示・パネル展開等で #game-screen の実寸が変わる
// 場合にもキャッシュを無効化し、stale な rect による座標ズレを防止する
// [task27] 注: canvas 内在サイズ（screen.width/height）は現在不変（HTML attribute 固定・JS 再代入なし）であるため
// 追加の無効化トリガーは不要。将来 canvas 内在サイズを変更する処理を追加する場合は
// 必ず window.invalidateGameScreenRect() を併呼すること
function invalidateGameScreenRect() {
    _gameScreenRectCacheValid = false;
}
window.invalidateGameScreenRect = invalidateGameScreenRect;

// [task19 v3] resize イベントでキャッシュを無効化
if (typeof window.addEventListener === 'function') {
    window.addEventListener('resize', () => { invalidateGameScreenRect(); });
}
let gameCtx = screen.getContext('2d');
const viewport = document.getElementById('viewport');
const status = document.getElementById('conn-status');
const fpsInfo = document.getElementById('fps-info');
const dataUsage = document.getElementById('data-usage');
const debug = document.getElementById('debug-info');
const cursor = document.getElementById('virtual-cursor');

// 拡大スコープ用Canvas要素および描画コンテキストの取得
const magCanvas = document.getElementById('magnifier-canvas');
const magCtx = magCanvas ? magCanvas.getContext('2d') : null;

//=============================================================================================
// 処理ブロック名 : updateScreen
// 概要           : 画面更新ルーティングを行う（差分WebP配信へ一本化）
//=============================================================================================
async function updateScreen() {
    await updateScreenDiff();
}

// 最終受領コンテンツ版数（サーバー応答ヘッダー X-Content-Version = lastWebp 更新回数）。
// 次回ポーリング URL に cver として付与し、新コンテンツなしなら 204 を受ける（HTTP 204 の無状態化）。
// 0 = 未受領（初回）。init=1 経路（初回・再同期）で full 受領後に 1 以上の値になる
let lastContentVersion = 0;

//=============================================================================================
// 処理ブロック名 : updateScreenDiff
// 概要           : 差分WebP方式で画面を更新する（HTTPモード用）
//=============================================================================================
async function updateScreenDiff() {
    const now = Date.now();

    // [task06] フォールバック: タイマースリップ検知
    // 前回のフレーム実行から (予定ディレイ + 5秒) 以上経過している場合、PCスリープから復帰したとみなす
    // 注: lastFrameStartTime は「最後に更新処理が**開始**された時刻」を記録する。
    // スリープ復帰検知には「関数呼出間隔」で十分であり、非同期処理の完了時刻ではない。
    const elapsedSinceLastFrame = now - lastFrameStartTime;
    if (elapsedSinceLastFrame > (lastScheduledDelay + SLEEP_SLIP_TOLERANCE_MS)) {
        console.log(`[SleepResume] Timer slip detected: elapsed=${elapsedSinceLastFrame}ms, scheduled=${lastScheduledDelay}ms`);
        // [v4] 現在すでにupdateScreenDiff()が実行中であるため、triggerResumeUpdate()を呼ばない。
        // 直接 isFirstWebpRequest = true をセットし、自身のフローでキーフレームを取得する。
        isFirstWebpRequest = true;
        if (typeof ProtocolManager !== 'undefined' && ProtocolManager.effectiveMode === 'ws') {
            ProtocolManager.sendWsControl({ action: 'init' });
        }
    }
    lastFrameStartTime = now;

    // [v4] ヘルパー: スリープ復帰ペンディングがある場合は即時ディレイ (0ms) を適用
    const getNextDelay = (calculatedDelay) => {
        if (triggerResumeUpdate._pendingImmediate) {
            triggerResumeUpdate._pendingImmediate = false;
            return 0;
        }
        return calculatedDelay;
    };

    // WebSocket モード時は HTTP ポーリングをスキップ
    if (ProtocolManager.effectiveMode === 'ws') {
        isUpdating = false;
        const nextDelay = getNextDelay(getDynamicDelay());
        lastScheduledDelay = nextDelay; // [task06] 予定ディレイを記録
        updateTimer = setTimeout(updateScreenDiff, nextDelay);
        return;
    }

    if (isUpdating) return;
    isUpdating = true;

    const startTime = Date.now();

    const controller = new AbortController();
    const timeoutId = setTimeout(() => controller.abort(), 2000);

    try {
        let url = `/screen.webp?t=${Date.now()}`;
        // [v4] URL生成時に isFirstWebpRequest を消費（レスポンス完了時の無条件上書きを防止）
        if (isFirstWebpRequest) {
            url += '&init=1';
            isFirstWebpRequest = false;
        }
        // 最終受領コンテンツ版数を付与（サーバー側で cver >= 現在値 → 204 判定）。
        // 初回（0）は付与しない — init=1 経路で必ず full を受領するため不要
        if (lastContentVersion > 0) {
            url += `&cver=${lastContentVersion}`;
        }
        const response = await fetch(url, { signal: controller.signal });
        clearTimeout(timeoutId);

        if (response.status === 401) {
            console.warn("Screen: Session expired. Redirecting to login...");
            if (window.__eventLog) window.__eventLog.consoleLog('warn', 'Screen: Session expired. Redirecting to login...', 'SCREEN_SESSION_EXPIRED');
            window.location.href = '/login-page';
            return;
        }

        if (response.status === 204) {
            if (typeof updateDataUsage === 'function') {
                updateDataUsage(getResponseHeaderBytes(response));
            }
            // 204 にも X-Content-Version が付与される（cver 判定結果の確定値）。次回リクエストへ引き継ぐ
            const cvNone = parseInt(response.headers.get('X-Content-Version'), 10);
            if (!isNaN(cvNone)) lastContentVersion = cvNone;
            // [v4] isFirstWebpRequest の消費はURL生成時に行うため、ここでの無条件上書きは削除
            // isFirstWebpRequest = false; // ← 削除
            isUpdating = false;

            const elapsed = Date.now() - startTime;
            const nextDelay = getNextDelay(Math.max(5, getDynamicDelay() - elapsed));
            lastScheduledDelay = nextDelay; // [task06] 予定ディレイを記録
            updateTimer = setTimeout(updateScreenDiff, nextDelay);
            return;
        }

        if (!response.ok) throw new Error(`Fetch failed: ${response.status}`);

        // [v4] isFirstWebpRequest の消費はURL生成時に行うため、ここでの無条件上書きは削除
        // isFirstWebpRequest = false; // ← 削除 (79行目)

        const rawSeqHeader = response.headers.get('X-Frame-Sequence');
        const httpSeq = (rawSeqHeader !== null && !isNaN(parseInt(rawSeqHeader, 10)))
            ? parseInt(rawSeqHeader, 10)
            : -1;

        // 200 応答にも X-Content-Version が付与される。次回リクエストの cver として引き継ぐ
        const cvFull = parseInt(response.headers.get('X-Content-Version'), 10);
        if (!isNaN(cvFull)) lastContentVersion = cvFull;
        const blob = await response.blob();

        updateDataUsage(blob.size + getResponseHeaderBytes(response));

        const img = new Image();

        // [v5] img.onload 全体を try...finally で保護（描画例外によるデッドロック防止）
        img.onload = () => {
            try {
                if (httpSeq >= 0) {
                    const diff = (httpSeq - latestSequenceNumber) | 0;
                    if (diff <= 0 && latestSequenceNumber !== 0) {
                        return;
                    }
                    latestSequenceNumber = httpSeq;
                }

                gameCtx.clearRect(0, 0, screen.width, screen.height);
                gameCtx.drawImage(img, 0, 0);

                drawMagnifier();

                status.textContent = 'SYNCHRONIZED';
                status.classList.add('ok');

                frameCount++;
                updateFpsDisplay();
            } catch (err) {
                console.error("[remote_screen] img.onload render error:", err);
                if (window.__eventLog) window.__eventLog.consoleLog('error', '[remote_screen] img.onload render error: ' + (err && err.message ? err.message : err), 'SCREEN_IMG_ERR');
            } finally {
                // [v5] 例外発生時も確実にリソース解放・更新フラグ解除・タイマー再予約
                URL.revokeObjectURL(img.src);
                isUpdating = false;

                const elapsed = Date.now() - startTime;
                const nextDelay = getNextDelay(Math.max(5, getDynamicDelay() - elapsed));
                lastScheduledDelay = nextDelay; // [task06] 予定ディレイを記録
                updateTimer = setTimeout(updateScreenDiff, nextDelay);
            }
        };

        img.onerror = () => {
            URL.revokeObjectURL(img.src);
            isUpdating = false;
            // [v4] isFirstWebpRequest は維持。次回の updateScreenDiff() でキーフレーム要求によりリカバリ
            // デコード失敗時はコンテンツ版数を不明状態に戻す。cver を保持したままだと
            // サーバーが「既受領」と誤判定して 204 を返し続け、画面が凍結する可能性がある
            lastContentVersion = 0;
            isFirstWebpRequest = true;
            // [v5 重要] _pendingImmediate の消費方針:
            // 画像ロードエラー時は「一度更新を試みた」とみなし、getNextDelay() を経由して
            // _pendingImmediate フラグを消費（false にリセット）する。
            // これにより、エラー発生後の次回のタイマー予約は即時ディレイ (0ms) が適用されるが、
            // その後のサイクルは通常の getDynamicDelay() に基づく遅延に戻される。
            // 理由: エラー時は「更新の試行は完了」であり、次回の即時更新を維持するよりも
            // 通常のタイマーサイクルに任せてサーバー負荷を抑制する設計が適切。
            const elapsed = Date.now() - startTime;
            const nextDelay = getNextDelay(Math.max(5, getDynamicDelay() - elapsed));
            lastScheduledDelay = nextDelay; // [task06] 予定ディレイを記録
            updateTimer = setTimeout(updateScreenDiff, nextDelay);
        };

        img.src = URL.createObjectURL(blob);

    } catch (error) {
        clearTimeout(timeoutId);
        isFirstWebpRequest = true; // 例外発生時のみ次回リトライでキーフレーム要求
        status.textContent = 'RE-SYNCING...';
        status.classList.remove('ok');
        isUpdating = false;

        const nextDelay = getNextDelay(1000);
        lastScheduledDelay = nextDelay; // [task06] 固定値1000msのため1000を記録
        updateTimer = setTimeout(updateScreenDiff, nextDelay);
    }
}

//=============================================================================================
// 処理ブロック名 : updateFpsDisplay
//=============================================================================================
function updateFpsDisplay() {
    const now = Date.now();
    const elapsed = now - lastFpsUpdate;
    if (elapsed >= 1000) {
        const fps = ((frameCount * 1000) / elapsed).toFixed(2);
        if (fpsInfo) fpsInfo.textContent = `${fps} FPS`;

        // ミニ情報に直接反映（system-monitor-barと完全一致）
        const hudMiniFps = document.getElementById('hud-mini-fps');
        if (hudMiniFps) {
            const fpsText = `${fps} FPS`; // e.g. "21.45 FPS"
            if (hudMiniFps.textContent !== fpsText) {
                hudMiniFps.textContent = fpsText;
            }
        }

        frameCount = 0;
        lastFpsUpdate = now;
    }
}

//=============================================================================================
// 処理ブロック名 : drawMagnifier
//=============================================================================================
function drawMagnifier() {
    if (!magCtx || !screen || screen.width === 0) return;

    const container = document.querySelector('.magnifier-container');
    if (!container) return;

    const rect = container.getBoundingClientRect();
    const canvasWidth = rect.width || 72;
    const canvasHeight = rect.height || 34;

    if (magCanvas.width !== canvasWidth || magCanvas.height !== canvasHeight) {
        magCanvas.width = canvasWidth;
        magCanvas.height = canvasHeight;
    }

    // [task27] content 基準（letterbox 適用後）: crop の CSS px → canvas バッファ px 変換を
    // content CSS 幅（例: right/left で 625px）基準で行う（box 幅 768px 基準だった現行の混合基準を解消）
    const screenRect = getCachedGameScreenRect();
    const displayWidth = screenRect.width || 1;

    const cropDisplayWidth = canvasWidth / targetZoom;
    const cropWidth = cropDisplayWidth * (screen.width / displayWidth);

    const cropHeight = cropWidth * (canvasHeight / canvasWidth);

    const cursorPixelX = vCursorX * screen.width;
    const cursorPixelY = vCursorY * screen.height;

    let idealSx = cursorPixelX - (cropWidth / 2);
    let idealSy = cursorPixelY - (cropHeight / 2);

    let actualCropWidth = Math.min(cropWidth, screen.width);
    let actualCropHeight = Math.min(cropHeight, screen.height);

    let sx = Math.max(0, Math.min(idealSx, screen.width - actualCropWidth));
    let sy = Math.max(0, Math.min(idealSy, screen.height - actualCropHeight));

    const cursorInCropX = cursorPixelX - sx;
    const cursorInCropY = cursorPixelY - sy;

    const targetInCanvasX = canvasWidth / 2;
    const targetInCanvasY = canvasHeight / 2;

    const scaleX = canvasWidth / actualCropWidth;
    const scaleY = canvasHeight / actualCropHeight;

    const dx = targetInCanvasX - cursorInCropX * scaleX;
    const dy = targetInCanvasY - cursorInCropY * scaleY;

    magCtx.clearRect(0, 0, canvasWidth, canvasHeight);
    magCtx.drawImage(
        screen,
        sx, sy, actualCropWidth, actualCropHeight,
        dx, dy, canvasWidth, canvasHeight
    );
}

//=============================================================================================
// 仮想カーソル表示＆拡大鏡描画の requestAnimationFrame 同期化
//=============================================================================================
function scheduleCursorUpdate() {
    if (isCursorUpdatePending) return;
    isCursorUpdatePending = true;
    requestAnimationFrame(() => {
        isCursorUpdatePending = false;
        updateCursorDisplay();
    });
}

function updateCursorDisplay() {
    // #virtual-cursor は親要素 #screen-container (position: relative) の子要素 (position: absolute) であるため、
    // 親要素の左上 (0,0) を基準としたローカルピクセル座標で配置する。
    // [task27] rect は content 基準（letterbox 適用後）。canvas box は #screen-container に 100%×100% で
    // 追随する（remote_base.css:103-106・実測でも canvas rect = container rect・task26 E10）ため、
    // container ローカル座標 = content の box 内オフセット + content 内相対位置。
    // [task27・M-2 反映] 前提: #game-screen（canvas box）の左上と #screen-container（position: relative）の左上が一致
    // していること（現状 CSS 上成立）。将来 #game-screen / #screen-container へ transform / margin / padding を
    // 追加する場合は本変換式を破るため再検証が必要。
    const rect = getCachedGameScreenRect();        // content rect
    const boxRect = getCachedGameScreenBoxRect();  // box rect（letterbox 込み）
    const x = (rect.left - boxRect.left) + vCursorX * rect.width;
    const y = (rect.top - boxRect.top) + vCursorY * rect.height;
    cursor.style.left = x + 'px';
    cursor.style.top = y + 'px';
    debug.textContent = `POS-X: ${Math.round(vCursorX * 100)}% POS-Y: ${Math.round(vCursorY * 100)}%`;
    drawMagnifier();
}

//=============================================================================================
// プライバシーフィルター設定（クライアントサイド適用版）
//=============================================================================================
function applyClientFilter(mode) {
    const gameScreen = document.getElementById('game-screen');
    gameScreen.classList.remove('filter-grayscale', 'filter-lowcontrast', 'filter-greenscale-night', 'filter-greenscale-military', 'filter-greenscale-phosphor');
    switch (mode) {
        case 1:
            gameScreen.classList.add('filter-grayscale');
            break;
        case 4:
            gameScreen.classList.add('filter-lowcontrast');
            break;
        default:
            break;
    }
}

//=============================================================================================
// 暗視フィルター設定（トグルサイクル方式）
//=============================================================================================
function applyNightvisionFilter(index) {
    const gameScreen = document.getElementById('game-screen');
    nightvisionModes.forEach(m => { if (m.className) gameScreen.classList.remove(m.className); });
    const mode = nightvisionModes[index];
    if (mode.className) {
        gameScreen.classList.add(mode.className);
    }
}

//=============================================================================================
// [task23 F-0] WebCodecs 経路専用の VideoDecoder 出力フレーム描画関数
// VideoFrame は Canvas 2D の ImageSource であり、drawImage() で描画可能
// （MDN VideoFrame: "A VideoFrame is an image source"）
// 責務分離: 本関数は「VideoFrame の描画 + frame.close()」に専念する。
// frameCount++ / updateFpsDisplay() / drawMagnifier() / status 更新は
// remote_connection.js の output コールバックに残留（WebP 経路末尾と同一の副次処理）。
// WebP 経路（decodeAndDraw / handleScreenUpdateBinary / handleWebpFrame 等）には
// 本関数を用いない（VideoFrame と ImageBitmap は別機構）。
// decodeAndDraw（remote_connection.js:740-824）は createImageBitmap 由来の
// ImageBitmap を描画する経路であり、本関数（VideoFrame 描画）とは別機構である。
//=============================================================================================
function drawVideoFrame(frame) {
    try {
        // [task23 指摘2] null frame 時は描画をスキップ（close は呼ばない・frame.close 不存在で TypeError を回避）
        if (!frame) return;
        if (!gameCtx || !screen) {
            console.error('[drawVideoFrame] gameCtx or screen is null - draw skipped');
            if (window.__eventLog) window.__eventLog.consoleLog('error', '[drawVideoFrame] gameCtx or screen is null - draw skipped', 'SCREEN_DRAW_CTX_NULL');
            return;
        }
        gameCtx.clearRect(0, 0, screen.width, screen.height);
        gameCtx.drawImage(frame, 0, 0, screen.width, screen.height);
        console.log('[drawVideoFrame] frame drawn', frame.displayWidth + 'x' + frame.displayHeight);
    } catch (e) {
        // [AGENTS.md NEVER] サイレントな例外の握りつぶし禁止
        console.error('[drawVideoFrame] draw error:', e);
        if (window.__eventLog) window.__eventLog.consoleLog('error', '[drawVideoFrame] draw error: ' + (e && e.message ? e.message : e), 'SCREEN_DRAW_ERR');
    } finally {
        // [task23 F-0-2] frame.close() の所有権は drawVideoFrame に単一化（drawImage の直後に解放）
        // MDN 公式パターン: ctx.drawImage(frame, 0, 0); frame.close();
        // W3C 仕様 "Close is final": close() 後の参照は InvalidStateError を投げるため、
        // output コールバック側の frame.close() は削除済み（二重 close を回避）。
        if (frame && typeof frame.close === 'function') {
            frame.close();
        }
    }
}

// [task23 F-0] remote_connection.js の output コールバック（デコード発火時）から
// 参照可能にするため window に明示公開（getCachedGameScreenRect と同一パターン）
window.drawVideoFrame = drawVideoFrame;
