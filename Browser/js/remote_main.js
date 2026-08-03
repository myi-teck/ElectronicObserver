//============================================================================================
// remote_main.js
// 初期化・イベントリスナー登録・ダイアログ制御
// 依存: remote_config.js, remote_connection.js, remote_screen.js, remote_input.js, remote_hud.js
//============================================================================================

//============================================================================================
// タイマーフォーマット関数
//============================================================================================
function formatTimer(milliseconds) {
    if (milliseconds < 0) {
        milliseconds = 0;
    }
    const totalSeconds = Math.floor(milliseconds / 1000);
    const seconds = String(totalSeconds).padStart(2, '0');
    const millis = String(Math.floor(milliseconds % 1000)).padStart(3, '0');
    return `${seconds}:${millis}`;
}

//============================================================================================
// 状態判定およびstateBadgeのクラス・テキスト更新関数
//============================================================================================
function updateStateBadge() {
    const elapsed = Date.now() - lastOperationTime;
    const stateBadge = document.getElementById('state-badge');

    if (elapsed < ACTIVE_TIMEOUT) {
        stateBadge.textContent = 'ACT';
        stateBadge.className = 'badge';
    } else if (elapsed < IDLE_TIMEOUT) {
        const idleElapsed = Math.max(0, Date.now() - lastOperationTime - ACTIVE_TIMEOUT);
        stateBadge.textContent = `IDL:${formatTimer(idleElapsed)}`;
        stateBadge.className = 'badge idle';
    } else {
        const sleepElapsed = Math.max(0, Date.now() - lastOperationTime - ACTIVE_TIMEOUT - IDLE_TIMEOUT);
        stateBadge.textContent = `SLP:${formatTimer(sleepElapsed)}`;
        stateBadge.className = 'badge sleep';
    }
}

//============================================================================================
// タイマー表示をリアルタイムに更新する独立した高頻度タイマー起動関数
//============================================================================================
function startTimerUpdate() {
    if (timerUpdateInterval !== null) return;
    timerUpdateInterval = setInterval(() => {
        const elapsed = Date.now() - lastOperationTime;
        const stateBadge = document.getElementById('state-badge');

        if (!autoFpsEnabled) {
            stateBadge.textContent = 'ACT';
            stateBadge.className = 'badge';
            lastSleepSeconds = -1;
            return;
        }

        if (elapsed < ACTIVE_TIMEOUT) {
            stateBadge.textContent = 'ACT';
            stateBadge.className = 'badge';
            lastSleepSeconds = -1;
        } else if (elapsed < IDLE_TIMEOUT) {
            const idleElapsed = Math.max(0, elapsed - ACTIVE_TIMEOUT);
            stateBadge.textContent = `IDL:${formatTimer(idleElapsed)}`;
            stateBadge.className = 'badge idle';
            lastSleepSeconds = -1;
        } else {
            const sleepElapsed = Math.max(0, elapsed - IDLE_TIMEOUT);
            const sleepSeconds = Math.floor(sleepElapsed / 1000);

            if (sleepSeconds !== lastSleepSeconds) {
                lastSleepSeconds = sleepSeconds;

                const sleepDisplayElapsed = sleepSeconds * 1000;
                const newTimerText = `SLP:${formatTimer(sleepDisplayElapsed)}`;
                stateBadge.textContent = newTimerText;

                stateBadge.className = 'badge sleep';
                void stateBadge.offsetWidth;
                stateBadge.className = 'badge sleep sleep-pulse';
            }
        }
    }, 100);
}

//============================================================================================
// 設定パネルの開閉トグル
//============================================================================================
const toggleBtn = document.getElementById('settings-toggle');
const settingsPanel = document.getElementById('settings-panel');

function toggleSettings(e) {
    e.preventDefault();
    e.stopPropagation();
    settingsPanel.classList.toggle('settings-closed');
}
toggleBtn.addEventListener('click', toggleSettings);
toggleBtn.addEventListener('pointerdown', e => {
    e.stopPropagation();
});

//============================================================================================
// 設定マスター・ディテール切り替えロジック
//============================================================================================
document.addEventListener('DOMContentLoaded', () => {
    const masterList = document.getElementById('settings-master-list');
    const detailArea = document.getElementById('settings-detail-area');

    if (!masterList || !detailArea) {
        console.warn('settings-master-list or settings-detail-area not found');
        return;
    }

    const masterItems = masterList.querySelectorAll('.master-item');

    if (masterItems.length === 0) {
        console.warn('No master-item elements found');
        return;
    }

    function selectSettingItem(targetId) {
        masterItems.forEach(item => item.classList.remove('active'));
        const activeItem = masterList.querySelector(`.master-item[data-target="${targetId}"]`);
        if (activeItem) activeItem.classList.add('active');

        const panels = detailArea.querySelectorAll('.detail-panel');
        panels.forEach(panel => panel.classList.remove('active'));
        const targetPanel = detailArea.querySelector(`#detail-${targetId}`);
        if (targetPanel) targetPanel.classList.add('active');
    }

    masterItems.forEach(item => {
        item.addEventListener('click', () => {
            selectSettingItem(item.dataset.target);
        });
    });

    selectSettingItem(masterItems[0].dataset.target);

    const scrollUpBtn = document.getElementById('settings-scroll-up');
    const scrollDownBtn = document.getElementById('settings-scroll-down');

    if (scrollUpBtn && scrollDownBtn && masterItems.length > 0) {
        const scrollAmount = 22;

        scrollUpBtn.addEventListener('click', () => {
            masterList.scrollBy({ top: -scrollAmount, behavior: 'smooth' });
        });
        scrollDownBtn.addEventListener('click', () => {
            masterList.scrollBy({ top: scrollAmount, behavior: 'smooth' });
        });
    }
});

//============================================================================================
// ボタンの初期状態の動的適用と切り替えイベントの登録ユーティリティ
//============================================================================================
function initSettingsButtons(groupId, currentValue, callback) {
    const group = document.getElementById(groupId);
    if (!group) return;
    const buttons = group.querySelectorAll('.settings-btn');

    buttons.forEach(btn => {
        const fpsAttr = btn.getAttribute('data-fps');
        const qualAttr = btn.getAttribute('data-quality');
        const autoAttr = btn.getAttribute('data-auto-fps');
        const sensAttr = btn.getAttribute('data-sensitivity');
        const zoomAttr = btn.getAttribute('data-zoom');
        const filterAttr = btn.getAttribute('data-filter');
        const dragDistAttr = btn.getAttribute('data-drag-distance');
        const tapTimeoutAttr = btn.getAttribute('data-tap-timeout');

        let btnValue;
        if (fpsAttr !== null) {
            btnValue = parseFloat(fpsAttr);
        } else if (qualAttr !== null) {
            btnValue = parseInt(qualAttr, 10);
        } else if (autoAttr !== null) {
            btnValue = parseInt(autoAttr, 10);
        } else if (sensAttr !== null) {
            btnValue = parseFloat(sensAttr);
        } else if (zoomAttr !== null) {
            btnValue = parseFloat(zoomAttr);
        } else if (filterAttr !== null) {
            btnValue = parseInt(filterAttr, 10);
        } else if (dragDistAttr !== null) {
            btnValue = parseInt(dragDistAttr, 10);
        } else if (tapTimeoutAttr !== null) {
            btnValue = parseInt(tapTimeoutAttr, 10);
        }

        if (btnValue === currentValue) {
            btn.classList.add('active');
        } else {
            btn.classList.remove('active');
        }

        btn.addEventListener('click', () => {
            buttons.forEach(b => b.classList.remove('active'));
            btn.classList.add('active');
            callback(btnValue);
        });
    });
}

//============================================================================================
// 各設定ボタンの初期化
//============================================================================================
initSettingsButtons('fps-group', targetFps, value => {
    targetFps = value;
    localStorage.setItem('targetFps', value);
    dataFps = value.toString();
    const delay = calculateDelayByFps(targetFps);
    ProtocolManager.syncFps(delay);
});

initSettingsButtons('quality-group', targetQuality, value => {
    targetQuality = value;
    localStorage.setItem('targetQuality', value);
    fetch(`/control?action=set_quality&q=${value}`);
});
// 起動時に現在の画質設定をC#側に確実に伝播させる初期化送信
fetch(`/control?action=set_quality&q=${targetQuality}`);

const autoFpsInitialValue = autoFpsEnabled ? 1 : 0;
initSettingsButtons('auto-fps-group', autoFpsInitialValue, value => {
    autoFpsEnabled = value === 1;
    localStorage.setItem('autoFpsEnabled', value.toString());

    lastOperationTime = Date.now();
    lastSleepSeconds = -1;

    const stateBadge = document.getElementById('state-badge');
    stateBadge.textContent = 'ACT';
    stateBadge.className = 'badge';

    clearTimeout(updateTimer);
    updateTimer = setTimeout(updateScreen, 50);
});

initSettingsButtons('sensitivity-group', targetSensitivity, value => {
    targetSensitivity = value;
    localStorage.setItem('targetSensitivity', value.toString());
});

//============================================================================================
// 通信プロトコル設定
//============================================================================================
(function initProtocolButtons() {
    const group = document.getElementById('protocol-group');
    if (!group) return;

    const buttons = group.querySelectorAll('.settings-btn');
    const currentMode = ProtocolManager.currentMode;

    buttons.forEach(btn => {
        const mode = btn.getAttribute('data-protocol');

        if (mode === currentMode) {
            btn.classList.add('active');
        } else {
            btn.classList.remove('active');
        }

        btn.addEventListener('click', () => {
            buttons.forEach(b => b.classList.remove('active'));
            btn.classList.add('active');
            ProtocolManager.setMode(mode);
        });
    });
})();

// ページロード時に ProtocolManager を初期化
ProtocolManager.init();

initSettingsButtons('zoom-group', targetZoom, value => {
    targetZoom = value;
    localStorage.setItem('targetZoom', value.toString());
    drawMagnifier();
});

//============================================================================================
// ドラッグ距離・タップ時間の初期化
//============================================================================================
initSettingsButtons('drag-distance-group', dragThreshold, value => {
    dragThreshold = value;
    localStorage.setItem('dragThreshold', value.toString());
});

initSettingsButtons('tap-timeout-group', tapTimeout, value => {
    tapTimeout = value;
    localStorage.setItem('tapTimeout', value.toString());
});

//============================================================================================
// 確認モーダル制御
//============================================================================================
const confirmModalOverlay = document.getElementById('confirm-modal-overlay');
const confirmModalMessage = document.getElementById('confirm-modal-message');
const confirmModalOk = document.getElementById('confirm-modal-ok');
const confirmModalCancel = document.getElementById('confirm-modal-cancel');
let confirmModalCallback = null;

function showConfirmModal(message, callback) {
    confirmModalMessage.textContent = message;
    confirmModalCallback = callback;
    confirmModalOverlay.classList.add('show');
}

function hideConfirmModal() {
    confirmModalOverlay.classList.remove('show');
    confirmModalCallback = null;
}

confirmModalOk.addEventListener('click', () => {
    if (confirmModalCallback) confirmModalCallback();
    hideConfirmModal();
});

confirmModalCancel.addEventListener('click', () => {
    hideConfirmModal();
});

confirmModalOverlay.addEventListener('click', (e) => {
    if (e.target === confirmModalOverlay) {
        hideConfirmModal();
    }
});

//============================================================================================
// ログインページ移動ボタン
//============================================================================================
const btnNavigateLogin = document.getElementById('btn-navigate-login');
btnNavigateLogin.addEventListener('click', (e) => {
    e.stopPropagation();
    showConfirmModal('ログインページへ移動しますか？', () => {
        fetch('/control?action=navigate_login');
    });
});

//============================================================================================
// フィルター・暗視の初期化
//============================================================================================
// ページロード時にフィルターを初期適用
applyClientFilter(currentFilterMode);

initSettingsButtons('filter-group', currentFilterMode, value => {
    currentFilterMode = value;
    localStorage.setItem('filterMode', value.toString());
    applyClientFilter(value);
});

// 暗視フィルター初期化
applyNightvisionFilter(currentNightvisionIndex);

const btnNightvision = document.getElementById('btn-nightvision');
function updateNightvisionButton() {
    btnNightvision.textContent = nightvisionModes[currentNightvisionIndex].name;
}
updateNightvisionButton();

btnNightvision.addEventListener('click', (e) => {
    e.stopPropagation();
    currentNightvisionIndex = (currentNightvisionIndex + 1) % nightvisionModes.length;
    localStorage.setItem('nightvisionIndex', currentNightvisionIndex.toString());
    applyNightvisionFilter(currentNightvisionIndex);
    updateNightvisionButton();
});

//============================================================================================
// 初期化処理
//============================================================================================
// リアルタイムタイマー更新ループを起動
startTimerUpdate();

// 画面更新ループを起動
updateScreen();

// HUD同期を起動
updateHud();

//============================================================================================
// 初期カーソル表示
//============================================================================================
window.addEventListener('resize', updateCursorDisplay);
updateCursorDisplay();

//============================================================================================
// 受領中任務HUDのトグルタイマー
//============================================================================================
setInterval(() => {
    window._showQuestProgress = !window._showQuestProgress;
    const rows = document.querySelectorAll('.quest-slot-row');
    rows.forEach(row => {
        if (!row.classList.contains('empty-quest-row')) {
            if (window._showQuestProgress) {
                row.classList.add('show-progress');
            } else {
                row.classList.remove('show-progress');
            }
        }
    });
}, 5000);

//============================================================================================
// 横画面用：操作パネルの折り畳み/展開トグル
//============================================================================================
(function initTrackpadPanel() {
    let isExpanded = false;

    const toggleBtn = document.getElementById('trackpad-toggle');
    const trackpadArea = document.getElementById('trackpad-area');
    const viewport = document.getElementById('viewport');
    if (!toggleBtn || !trackpadArea) return;

    function closePanel() {
        isExpanded = false;
        trackpadArea.classList.remove('panel-expanded');
        toggleBtn.classList.remove('panel-expanded');
        toggleBtn.setAttribute('aria-expanded', 'false');
        if (viewport) {
            viewport.classList.remove('overlay-active');
        }
    }

    function togglePanel(e) {
        if (e) {
            e.preventDefault();
            e.stopPropagation();
        }
        isExpanded = !isExpanded;

        if (isExpanded) {
            trackpadArea.classList.add('panel-expanded');
            toggleBtn.classList.add('panel-expanded');
            toggleBtn.setAttribute('aria-expanded', 'true');
            if (viewport) {
                viewport.classList.add('overlay-active');
            }
        } else {
            closePanel();
        }
    }

    toggleBtn.addEventListener('click', (e) => {
        togglePanel(e);
    });
    toggleBtn.addEventListener('pointerdown', e => {
        e.stopPropagation();
    });

    function resetPanelState() {
        closePanel();
    }

    if (screen && screen.orientation) {
        screen.orientation.addEventListener('change', resetPanelState);
    }

    window.addEventListener('orientationchange', resetPanelState);
})();
