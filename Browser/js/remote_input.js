//============================================================================================
// remote_input.js
// トラックパッド・タップ・マウスイベント制御
// 依存: remote_config.js, remote_connection.js, remote_screen.js
//============================================================================================

// ゲーム画面タッチ/マウス操作
let viewportTouchStartX = 0;
let viewportTouchStartY = 0;
let viewportTouchStartTime = 0;
let isDraggingOnViewport = false;
let isMouseDownOnViewport = false;

function activateViewportFpsControl() {
    const isSlowFpsMode = (Date.now() - lastOperationTime) >= ACTIVE_TIMEOUT;
    lastOperationTime = Date.now();
    if (isSlowFpsMode) {
        clearTimeout(updateTimer);
        updateTimer = setTimeout(updateScreen, 50);
    }
    const stateBadge = document.getElementById('state-badge');
    stateBadge.textContent = 'ACT';
    stateBadge.className = 'badge';
}

function handleViewportMove(clientX, clientY) {
    const rect = screen.getBoundingClientRect();
    const x = Math.max(0, Math.min(1.0, (clientX - rect.left) / rect.width));
    const y = Math.max(0, Math.min(1.0, (clientY - rect.top) / rect.height));
    vCursorX = x;
    vCursorY = y;
    scheduleCursorUpdate();
    sendMoveControl(x, y);
}

// viewport touchstart
// 【二次レビュー💡指摘3対応】 passive: false について:
// このイベントはタッチサイクル中のゲーム操作防止（preventDefault）のために passive: false を必要とする。
// touch-action: none が #viewport に設定されている場合、デフォルトアクションは既に無効化されており、
// preventDefault() の呼び出しは安全である。一部のブラウザで「passive:false イベントで preventDefault を
// 呼んだがデフォルトアクションが存在しない」警告が出る可能性があるが、機能的な影響はない。
viewport.addEventListener('touchstart', (e) => {
    // HUD展開中のタップであれば、パネルをたたんでゲームアクション送信をスキップ
    // dismissIfExpanded は remote_main.js 側の initTrackpadPanel IIFE 内で定義・公開
    if (typeof window.dismissIfExpanded === 'function' && window.dismissIfExpanded(e)) {
        return;
    }

    const touch = e.touches[0];
    viewportTouchStartX = touch.clientX;
    viewportTouchStartY = touch.clientY;
    viewportTouchStartTime = Date.now();
    isDraggingOnViewport = false;
    activateViewportFpsControl();
    handleViewportMove(touch.clientX, touch.clientY);
}, { passive: false });

// viewport touchmove
viewport.addEventListener('touchmove', (e) => {
    const touch = e.touches[0];
    const dx = touch.clientX - viewportTouchStartX;
    const dy = touch.clientY - viewportTouchStartY;
    const dist = Math.sqrt(dx * dx + dy * dy);

    if (dist > dragThreshold && !isDraggingOnViewport) {
        isDraggingOnViewport = true;
        sendControlAction('mousedown');
    }

    handleViewportMove(touch.clientX, touch.clientY);
}, { passive: true });

// viewport touchend
viewport.addEventListener('touchend', (e) => {
    // 注意: touchend では dismissIfExpanded(e) を呼ばない（二次レビュー💡指摘4対応）。
    // パネル折り畳みは touchstart/click 時点で完了しているため、touchend では
    // isDismissingPanel フラグによるゲームアクションのキャンセルのみを行う。
    // isDismissingPanel のリセットはここで実行（クリティカルレビュー指摘2対応）。
    if (window.isDismissingPanel) {
        window.isDismissingPanel = false;
        return;
    }

    const elapsed = Date.now() - viewportTouchStartTime;

    if (isDraggingOnViewport) {
        sendControlAction('mouseup');
    } else if (elapsed <= tapTimeout) {
        const rect = screen.getBoundingClientRect();
        const x = Math.max(0, Math.min(1.0, (viewportTouchStartX - rect.left) / rect.width));
        const y = Math.max(0, Math.min(1.0, (viewportTouchStartY - rect.top) / rect.height));
        sendControlAction('touch_click', x, y);
    }

    isDraggingOnViewport = false;
}, { passive: true });

// viewport mousedown
viewport.addEventListener('mousedown', (e) => {
    if (e.button !== 0) return;

    // HUD展開中のクリックであれば、パネルをたたんでゲームアクション送信をスキップ
    // dismissIfExpanded は remote_main.js 側の initTrackpadPanel IIFE 内で定義・公開
    if (typeof window.dismissIfExpanded === 'function' && window.dismissIfExpanded(e)) {
        return;
    }

    e.preventDefault();

    isMouseDownOnViewport = true;
    viewportTouchStartX = e.clientX;
    viewportTouchStartY = e.clientY;
    viewportTouchStartTime = Date.now();

    sendControlAction('mousedown');
    activateViewportFpsControl();
    handleViewportMove(e.clientX, e.clientY);
});

// viewport mousemove
viewport.addEventListener('mousemove', (e) => {
    if (!isMouseDownOnViewport) return;
    handleViewportMove(e.clientX, e.clientY);
});

// viewport mouseup
viewport.addEventListener('mouseup', (e) => {
    if (!isMouseDownOnViewport) return;
    isMouseDownOnViewport = false;

    sendControlAction('mouseup');

    const elapsed = Date.now() - viewportTouchStartTime;
    const dx = e.clientX - viewportTouchStartX;
    const dy = e.clientY - viewportTouchStartY;
    const dist = Math.sqrt(dx * dx + dy * dy);

    if (elapsed <= tapTimeout && dist <= dragThreshold) {
        // タップ判定
    }
});

//============================================================================================
// トラックパッド操作
//============================================================================================
const pad = document.getElementById('trackpad');

let lastX = 0, lastY = 0;

pad.addEventListener('pointerdown', e => {
    lastX = e.clientX;
    lastY = e.clientY;
    pad.setPointerCapture(e.pointerId);

    if (magnifierTimer) {
        clearTimeout(magnifierTimer);
    }

    magnifierTimer = setTimeout(() => {
        const magnifier = document.querySelector('.magnifier-container');
        if (magnifier) {
            magnifier.classList.add('visible');
        }
    }, 1000);
});

pad.addEventListener('pointerup', e => {
    if (magnifierTimer) {
        clearTimeout(magnifierTimer);
        magnifierTimer = null;
    }
    const magnifier = document.querySelector('.magnifier-container');
    if (magnifier) {
        magnifier.classList.remove('visible');
    }
});

pad.addEventListener('pointercancel', e => {
    if (magnifierTimer) {
        clearTimeout(magnifierTimer);
        magnifierTimer = null;
    }
    const magnifier = document.querySelector('.magnifier-container');
    if (magnifier) {
        magnifier.classList.remove('visible');
    }
});

pad.addEventListener('pointermove', e => {
    if (e.buttons === 0) return;

    const isSlowFpsMode = (Date.now() - lastOperationTime) >= ACTIVE_TIMEOUT;
    lastOperationTime = Date.now();

    if (isSlowFpsMode) {
        clearTimeout(updateTimer);
        updateTimer = setTimeout(updateScreen, 50);
    }
    const stateBadge = document.getElementById('state-badge');
    stateBadge.textContent = 'ACT';
    stateBadge.className = 'badge';
    const dx = e.clientX - lastX;
    const dy = e.clientY - lastY;
    lastX = e.clientX;
    lastY = e.clientY;

    const baseSensitivity = 0.002;
    const sensitivity = baseSensitivity * targetSensitivity;
    vCursorX = Math.max(0, Math.min(1.0, vCursorX + dx * sensitivity));
    vCursorY = Math.max(0, Math.min(1.0, vCursorY + dy * sensitivity));

    scheduleCursorUpdate();
    sendMoveControl(vCursorX, vCursorY);
});

//============================================================================================
// クリックボタン操作
//============================================================================================
const clickBtn = document.getElementById('click-btn');
const clickBtnElement = document.getElementById('click-btn');

clickBtnElement.addEventListener('pointerdown', e => {
    e.preventDefault();
    clickBtnElement.classList.add('clicked');

    const isSlowFpsMode = (Date.now() - lastOperationTime) >= ACTIVE_TIMEOUT;
    lastOperationTime = Date.now();

    if (isSlowFpsMode) {
        clearTimeout(updateTimer);
        updateTimer = setTimeout(updateScreen, 50);
    }
    const stateBadge = document.getElementById('state-badge');
    stateBadge.textContent = 'ACT';
    stateBadge.className = 'badge';
    sendControlAction('click');
});

clickBtnElement.addEventListener('pointerup', e => {
    clickBtnElement.classList.remove('clicked');
});

clickBtnElement.addEventListener('pointercancel', e => {
    clickBtnElement.classList.remove('clicked');
});

//============================================================================================
// 艦隊ステータスHUDのシングルクリックで艦隊詳細HUDを切り替え
//============================================================================================
(function() {
    const fleetHud = document.getElementById('fleet-hud');
    if (!fleetHud) return;

    fleetHud.addEventListener('pointerdown', e => {
        e.stopPropagation();
    });

    fleetHud.addEventListener('click', (e) => {
        e.preventDefault();
        e.stopPropagation();
        switchFleetDetail();
    });

    fleetHud.addEventListener('touchend', (e) => {
        if (e.cancelable) {
            e.preventDefault();
        }
        e.stopPropagation();
        switchFleetDetail();
    }, { passive: false });
})();

//============================================================================================
// 艦隊詳細HUDのタップで表示モードをトグル切り替え
//============================================================================================
(function() {
    const detailPanel = document.getElementById('fleet-detail-panel');
    if (!detailPanel) return;

    detailPanel.addEventListener('pointerdown', e => {
        e.stopPropagation();
    });

    detailPanel.addEventListener('click', (e) => {
        e.preventDefault();
        e.stopPropagation();
        window._showFleetEquipment = !window._showFleetEquipment;
        renderFleetDetail(currentDetailFleetId);
    });

    detailPanel.addEventListener('touchend', (e) => {
        if (e.cancelable) {
            e.preventDefault();
        }
        e.stopPropagation();
        window._showFleetEquipment = !window._showFleetEquipment;
        renderFleetDetail(currentDetailFleetId);
    }, { passive: false });
})();
