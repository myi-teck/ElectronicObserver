//============================================================================================
// remote_screen.js
// Canvas差分WebP描画・拡大鏡・画面フィルター
// 依存: remote_config.js, remote_connection.js
//============================================================================================

// DOM要素の取得（ページ読み込み時に実行される）
const screen = document.getElementById('game-screen');
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

//============================================================================================
// 処理ブロック名 : updateScreen
// 概要           : 画面更新ルーティングを行う（差分WebP配信へ一本化）
//============================================================================================
async function updateScreen() {
    await updateScreenDiff();
}

//============================================================================================
// 処理ブロック名 : updateScreenDiff
// 概要           : 差分WebP方式で画面を更新する（HTTPモード用）
//============================================================================================
async function updateScreenDiff() {
    // WebSocket モード時は HTTP ポーリングをスキップ
    if (ProtocolManager.effectiveMode === 'ws') {
        isUpdating = false;
        const nextDelay = getDynamicDelay();
        updateTimer = setTimeout(updateScreen, nextDelay);
        return;
    }

    if (isUpdating) return;
    isUpdating = true;

    const startTime = Date.now();

    const controller = new AbortController();
    const timeoutId = setTimeout(() => controller.abort(), 2000);

    try {
        let url = `/screen.webp?t=${Date.now()}`;
        if (isFirstWebpRequest) {
            url += '&init=1';
        }
        const response = await fetch(url, { signal: controller.signal });
        clearTimeout(timeoutId);

        if (response.status === 401) {
            console.warn("Screen: Session expired. Redirecting to login...");
            window.location.href = '/login-page';
            return;
        }

        if (response.status === 204) {
            if (typeof updateDataUsage === 'function') {
                updateDataUsage(getResponseHeaderBytes(response));
            }
            isFirstWebpRequest = false;
            isUpdating = false;

            const elapsed = Date.now() - startTime;
            const nextDelay = Math.max(5, getDynamicDelay() - elapsed);
            updateTimer = setTimeout(updateScreen, nextDelay);
            return;
        }

        if (!response.ok) throw new Error(`Fetch failed: ${response.status}`);

        isFirstWebpRequest = false;

        const rawSeqHeader = response.headers.get('X-Frame-Sequence');
        const httpSeq = (rawSeqHeader !== null && !isNaN(parseInt(rawSeqHeader, 10)))
            ? parseInt(rawSeqHeader, 10)
            : -1;
        const blob = await response.blob();

        updateDataUsage(blob.size + getResponseHeaderBytes(response));

        const img = new Image();

        img.onload = () => {
            if (httpSeq >= 0) {
                const diff = (httpSeq - latestSequenceNumber) | 0;
                if (diff <= 0 && latestSequenceNumber !== 0) {
                    URL.revokeObjectURL(img.src);
                    isUpdating = false;
                    const elapsed = Date.now() - startTime;
                    const nextDelay = Math.max(5, getDynamicDelay() - elapsed);
                    updateTimer = setTimeout(updateScreen, nextDelay);
                    return;
                }
                latestSequenceNumber = httpSeq;
            }

            gameCtx.clearRect(0, 0, screen.width, screen.height);
            gameCtx.drawImage(img, 0, 0);

            drawMagnifier();

            URL.revokeObjectURL(img.src);

            status.textContent = 'SYNCHRONIZED';
            status.classList.add('ok');

            frameCount++;
            updateFpsDisplay();
            isUpdating = false;

            const elapsed = Date.now() - startTime;
            const nextDelay = Math.max(5, getDynamicDelay() - elapsed);
            updateTimer = setTimeout(updateScreen, nextDelay);
        };

        img.onerror = () => {
            URL.revokeObjectURL(img.src);
            isUpdating = false;

            const elapsed = Date.now() - startTime;
            const nextDelay = Math.max(5, getDynamicDelay() - elapsed);
            updateTimer = setTimeout(updateScreen, nextDelay);
        };

        img.src = URL.createObjectURL(blob);

    } catch (error) {
        clearTimeout(timeoutId);
        isFirstWebpRequest = true;
        status.textContent = 'RE-SYNCING...';
        status.classList.remove('ok');
        isUpdating = false;

        updateTimer = setTimeout(updateScreen, 1000);
    }
}

//============================================================================================
// 処理ブロック名 : updateFpsDisplay
//============================================================================================
function updateFpsDisplay() {
    const now = Date.now();
    const elapsed = now - lastFpsUpdate;
    if (elapsed >= 1000) {
        const fps = ((frameCount * 1000) / elapsed).toFixed(2);
        fpsInfo.textContent = `${fps} FPS`;
        frameCount = 0;
        lastFpsUpdate = now;
    }
}

//============================================================================================
// 処理ブロック名 : drawMagnifier
//============================================================================================
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

    const screenRect = screen.getBoundingClientRect();
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

//============================================================================================
// 仮想カーソル表示＆拡大鏡描画の requestAnimationFrame 同期化
//============================================================================================
function scheduleCursorUpdate() {
    if (isCursorUpdatePending) return;
    isCursorUpdatePending = true;
    requestAnimationFrame(() => {
        isCursorUpdatePending = false;
        updateCursorDisplay();
    });
}

function updateCursorDisplay() {
    const rect = screen.getBoundingClientRect();
    const x = rect.left + (vCursorX * rect.width);
    const y = rect.top + (vCursorY * rect.height);
    cursor.style.left = x + 'px';
    cursor.style.top = y + 'px';
    debug.textContent = `POS-X: ${Math.round(vCursorX*100)}% POS-Y: ${Math.round(vCursorY*100)}%`;
    drawMagnifier();
}

//============================================================================================
// プライバシーフィルター設定（クライアントサイド適用版）
//============================================================================================
function applyClientFilter(mode) {
    const gameScreen = document.getElementById('game-screen');
    gameScreen.classList.remove('filter-grayscale', 'filter-lowcontrast', 'filter-greenscale-night', 'filter-greenscale-military', 'filter-greenscale-phosphor');
    switch(mode) {
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

//============================================================================================
// 暗視フィルター設定（トグルサイクル方式）
//============================================================================================
function applyNightvisionFilter(index) {
    const gameScreen = document.getElementById('game-screen');
    nightvisionModes.forEach(m => { if (m.className) gameScreen.classList.remove(m.className); });
    const mode = nightvisionModes[index];
    if (mode.className) {
        gameScreen.classList.add(mode.className);
    }
}
