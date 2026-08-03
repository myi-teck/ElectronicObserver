//============================================================================================
// remote_config.js
// グローバル定数・設定ストレージ・初期値の定義
// 依存: なし（最優先読み込み）
//============================================================================================

// 動的FPS制御用定数
const ACTIVE_TIMEOUT = 15000;         // アクティブ→アイドル遷移までの時間（15秒）
const IDLE_TIMEOUT = 45000;           // アクティブ→スリープ遷移までの合計時間（45秒）
const IDLE_FPS = 0.5;                 // アイドル時のFPS（2秒に1回）
const SLEEP_FPS = (1.0 / 60);         // スリープ時のFPS（1分に1回）

// 通信量計測関連定数
const USE_DYNAMIC_HEADER_CALC = true;
const HTTP_HEADER_OVERHEAD_FIXED = 600; // 固定値モード時のHTTPヘッダー概算サイズ (Byte)
const WS_FRAME_OVERHEAD = 4;            // WebSocketフレームヘッダーの概算サイズ (Byte)

// 通信量集計用グローバル変数 (バイト単位)
let accumulatedBytes = 0;

// FPS実測用の変数定義
let frameCount = 0;
let lastFpsUpdate = Date.now();
let isFirstWebpRequest = true;
let isUpdating = false;

// [Phase 4] グローバルシーケンス番号（WebSocket/HTTP共通）
let latestSequenceNumber = 0;

// 艦隊詳細HUD関連
let currentDetailFleetId = 1;
let lastRenderedFleetId = -1;
let lastRenderedShipsJson = "";
let lastRenderedMode = "";

// 動的FPS制御用変数
let lastOperationTime = Date.now();
let updateTimer = null;

// [案A-1] dataFps: 現在アクティブなFPSボタンの data-fps 属性値（文字列）
let dataFps = "60";
let lastSyncedDelay = -1;

// 前回のSLEEPタイマー表示秒数
let lastSleepSeconds = -1;

// タイマーリアルタイム更新用インターバルID
let timerUpdateInterval = null;

// 仮想カーソル関連
let isCursorUpdatePending = false;

// 拡大鏡表示用タイマーID
let magnifierTimer = null;

// 艦隊詳細HUDの表示モードフラグ
window._showFleetEquipment = false;

// 受領中任務HUDのトグルフラグ
window._showQuestProgress = false;
window._lastQuestsKey = null;

// クライアント側で現在適用されている各HUDブロックの最新バージョン
let hudVersions = {
    material: -1,
    fleets: -1,
    battle: -1,
    combat: -1,
    quests: -1,
    logs: -1
};

// 移動制御通信の最適化用フラグ
let isMoveSending = false;
let pendingMoveCoords = null;

// 暗視フィルター用
const nightvisionModes = [
    { name: 'なし', className: null },
    { name: '暗視カメラ風', className: 'filter-greenscale-night' },
    { name: '軍用モニター風', className: 'filter-greenscale-military' },
    { name: 'フォスファイト風', className: 'filter-greenscale-phosphor' },
];
let currentNightvisionIndex = parseInt(localStorage.getItem('nightvisionIndex')) || 0;

// フィルターモードの初期値
let currentFilterMode = parseInt(localStorage.getItem('filterMode')) || 0;

// 設定値の初期状態：localStorageから前回値を取得
let targetFps = parseFloat(localStorage.getItem('targetFps')) || 10;
dataFps = targetFps.toString();
let targetQuality = parseInt(localStorage.getItem('targetQuality'), 10) || 40;
let autoFpsEnabled = localStorage.getItem('autoFpsEnabled') === '1';
let targetSensitivity = parseFloat(localStorage.getItem('targetSensitivity')) || 1.0;
let targetZoom = parseFloat(localStorage.getItem('targetZoom')) || 3.0;
let dragThreshold = parseInt(localStorage.getItem('dragThreshold'), 10) || 10;
let tapTimeout = parseInt(localStorage.getItem('tapTimeout'), 10) || 300;

// 仮想カーソル位置
let vCursorX = 0.5, vCursorY = 0.5;
