//============================================================================================
// remote_hud.js
// HUDデータ受領・動的HTML構造レンダリング
// 依存: remote_config.js, remote_connection.js
//============================================================================================

//============================================================================================
// 艦隊詳細HUDのシフト切り替え関数
//============================================================================================
function switchFleetDetail() {
    const fleets = window._fleetData;
    const fleetCount = fleets ? fleets.length : 4;

    currentDetailFleetId = currentDetailFleetId >= fleetCount ? 1 : currentDetailFleetId + 1;

    updateFleetHudActiveState();
    renderFleetDetail(currentDetailFleetId);
}

//============================================================================================
// 艦隊ステータスHUDの強調表示（activeクラス）を即時更新
//============================================================================================
function updateFleetHudActiveState() {
    const fleetHud = document.getElementById('fleet-hud');
    if (!fleetHud) return;

    const items = fleetHud.querySelectorAll('.fleet-hud-item');
    items.forEach(item => {
        const numSpan = item.querySelector('.fleet-num');
        if (!numSpan) return;
        const fleetId = parseInt(numSpan.textContent, 10);

        if (fleetId === currentDetailFleetId) {
            item.classList.add('active');
        } else {
            item.classList.remove('active');
        }
    });
}

//============================================================================================
// 処理ブロック名 : renderFleetDetail
//============================================================================================
function renderFleetDetail(fleetId) {
    const tbody = document.querySelector('#ship-table tbody');
    const theadRow = document.querySelector('#ship-table thead tr');
    if (!tbody || !theadRow) return;

    const fleets = window._fleetData;
    if (!fleets || fleets.length === 0) return;

    const fleet = fleets.find(f => f.id === fleetId);
    if (!fleet || !fleet.ships || fleet.ships.length === 0) {
        const colspan = window._showFleetEquipment ? '9' : '6';
        tbody.innerHTML = `
            <tr>
                <td colspan="${colspan}" style="text-align: center; padding: 10px; color: rgba(163, 247, 191, 0.5);">
                    艦船データなし
                </td>
            </tr>
        `;
        updateFleetHeader(theadRow);
        return;
    }

    const compareShips = fleet.ships.map(ship => {
        if (!ship) return null;
        return {
            name: ship.name,
            level: ship.level,
            condition: ship.condition,
            hp: ship.hp,
            maxHp: ship.maxHp,
            fuel: ship.fuel,
            fuelMax: ship.fuelMax,
            ammo: ship.ammo,
            ammoMax: ship.ammoMax,
            equipment: ship.equipment
        };
    });

    const currentShipsJson = JSON.stringify(compareShips);
    const currentMode = window._showFleetEquipment ? 'equip' : 'hp';
    if (lastRenderedFleetId === fleetId && lastRenderedShipsJson === currentShipsJson && lastRenderedMode === currentMode) {
        return;
    }

    lastRenderedFleetId = fleetId;
    lastRenderedShipsJson = currentShipsJson;
    lastRenderedMode = currentMode;

    updateFleetHeader(theadRow);

    let html = '';

    if (window._showFleetEquipment) {
        // 装備モード（9列）
        fleet.ships.forEach((ship) => {
            if (!ship) {
                html += `
                    <tr class="empty-slot-row">
                        <td class="ship-name-cell" colspan="9" style="color: rgba(163, 247, 191, 0.3);">--- 空スロット ---</td>
                    </tr>
                `;
                return;
            }

            const shipName = ship.name || '---';
            const nameClass = shipName.length > 3 ? 'ship-name-scroll scroll' : 'ship-name-scroll';
            const nameHtml = `
                <div class="ship-name-container" title="${shipName}">
                    <span class="${nameClass}" data-name="${shipName}">${shipName}</span>
                </div>
            `;

            let conditionClass = 'ship-condition-cell condition-ok';
            if (ship.condition < 50) {
                conditionClass = 'ship-condition-cell condition-fatigue';
            } else if (ship.condition < 70) {
                conditionClass = 'ship-condition-cell condition-warning';
            }

            const equipment = ship.equipment || [];
            const slots = [];
            for (let i = 0; i < 6; i++) {
                slots.push(equipment[i] ?? null);
            }

            html += `
                <tr>
                    <td class="ship-name-cell">${nameHtml}</td>
                    <td class="ship-level-cell">${ship.level || '-'}</td>
                    <td class="${conditionClass}">${ship.condition || '-'}</td>
                    <td class="ship-equip-cell">${renderEquipSlot(slots[0])}</td>
                    <td class="ship-equip-cell">${renderEquipSlot(slots[1])}</td>
                    <td class="ship-equip-cell">${renderEquipSlot(slots[2])}</td>
                    <td class="ship-equip-cell">${renderEquipSlot(slots[3])}</td>
                    <td class="ship-equip-cell">${renderEquipSlot(slots[4])}</td>
                    <td class="ship-equip-cell">${renderEquipSlot(slots[5])}</td>
                </tr>
            `;
        });
    } else {
        // HPモード（6列）
        fleet.ships.forEach((ship) => {
            if (!ship) {
                html += `
                    <tr class="empty-slot-row">
                        <td class="ship-name-cell" colspan="6" style="color: rgba(163, 247, 191, 0.3);">--- 空スロット ---</td>
                    </tr>
                `;
                return;
            }

            const hpRatio = ship.maxHp > 0 ? ship.hp / ship.maxHp : 0;
            const hpSegments = Math.round(hpRatio * 10);
            let hpBarClass = 'segment-bar-fill';
            let hpStateClass = '';

            if (hpRatio <= 0.25) {
                hpBarClass += ' critical';
                hpStateClass = 'hp-state-critical';
            } else if (hpRatio <= 0.50) {
                hpBarClass += ' warning';
                hpStateClass = 'hp-state-moderate';
            } else if (hpRatio <= 0.75) {
                hpStateClass = 'hp-state-minor';
            }

            let hpGaugeHtml = `<div class="resource-segment-gauge">`;
            for (let i = 1; i <= 10; i++) {
                const activeClass = i <= hpSegments ? `active ${hpBarClass}` : '';
                hpGaugeHtml += `<span class="gauge-segment ${activeClass}"></span>`;
            }
            hpGaugeHtml += `</div>`;

            let conditionClass = 'ship-condition-cell condition-ok';
            if (ship.condition < 50) {
                conditionClass = 'ship-condition-cell condition-fatigue';
            } else if (ship.condition < 70) {
                conditionClass = 'ship-condition-cell condition-warning';
            }

            const shipName = ship.name || '---';
            const nameClass = shipName.length > 3 ? 'ship-name-scroll scroll' : 'ship-name-scroll';
            const nameHtml = `
                <div class="ship-name-container" title="${shipName}">
                    <span class="${nameClass}" data-name="${shipName}">${shipName}</span>
                </div>
            `;

            const fuelRatio = ship.fuelMax > 0 ? ship.fuel / ship.fuelMax : 0;
            const fuelSegments = Math.round(fuelRatio * 10);
            let fuelBarClass = 'segment-bar-fill';
            if (fuelRatio < 0.20) fuelBarClass += ' critical';
            else if (fuelRatio < 0.50) fuelBarClass += ' warning';

            let fuelGaugeHtml = `<div class="resource-segment-gauge">`;
            for (let i = 1; i <= 10; i++) {
                const activeClass = i <= fuelSegments ? `active ${fuelBarClass}` : '';
                fuelGaugeHtml += `<span class="gauge-segment ${activeClass}"></span>`;
            }
            fuelGaugeHtml += `</div>`;

            const ammoRatio = ship.ammoMax > 0 ? ship.ammo / ship.ammoMax : 0;
            const ammoSegments = Math.round(ammoRatio * 10);
            let ammoBarClass = 'segment-bar-fill';
            if (ammoRatio < 0.20) ammoBarClass += ' critical';
            else if (ammoRatio < 0.50) ammoBarClass += ' warning';

            let ammoGaugeHtml = `<div class="resource-segment-gauge">`;
            for (let i = 1; i <= 10; i++) {
                const activeClass = i <= ammoSegments ? `active ${ammoBarClass}` : '';
                ammoGaugeHtml += `<span class="gauge-segment ${activeClass}"></span>`;
            }
            ammoGaugeHtml += `</div>`;

            html += `
                <tr>
                    <td class="ship-name-cell">${nameHtml}</td>
                    <td class="ship-level-cell">${ship.level || '-'}</td>
                    <td class="${conditionClass}">${ship.condition || '-'}</td>
                    <td class="ship-hp-cell">
                        <div class="ship-cell-container ${hpStateClass}">
                            <span class="ship-hp-text">${ship.hp}/${ship.maxHp}</span>
                            ${hpGaugeHtml}
                        </div>
                    </td>
                    <td class="ship-resource-cell" title="FUEL: ${ship.fuel}/${ship.fuelMax} (${Math.round(fuelRatio*100)}%)">
                        <div class="ship-cell-container">
                            <span class="ship-resource-text">${(ship.fuel ?? '-').toLocaleString()}/${(ship.fuelMax ?? '-').toLocaleString()}</span>
                            ${fuelGaugeHtml}
                        </div>
                    </td>
                    <td class="ship-resource-cell" title="AMMO: ${ship.ammo}/${ship.ammoMax} (${Math.round(ammoRatio*100)}%)">
                        <div class="ship-cell-container">
                            <span class="ship-resource-text">${(ship.ammo ?? '-').toLocaleString()}/${(ship.ammoMax ?? '-').toLocaleString()}</span>
                            ${ammoGaugeHtml}
                        </div>
                    </td>
                </tr>
            `;
        });
    }

    tbody.innerHTML = html;
}

//============================================================================================
// 処理ブロック名 : updateFleetHeader
//============================================================================================
function updateFleetHeader(theadRow) {
    if (window._showFleetEquipment) {
        theadRow.innerHTML = `
            <th>NAME</th>
            <th>LV</th>
            <th>CND</th>
            <th>S1</th>
            <th>S2</th>
            <th>S3</th>
            <th>S4</th>
            <th>S5</th>
            <th>EX</th>
        `;
    } else {
        theadRow.innerHTML = `
            <th>NAME</th>
            <th>LV</th>
            <th>CND</th>
            <th>HP</th>
            <th>FEL</th>
            <th>AMO</th>
        `;
    }
}

//============================================================================================
// 処理ブロック名 : renderEquipSlot
//============================================================================================
function renderEquipSlot(equip) {
    if (!equip) return '-';
    let name;
    let isExpansion = false;

    if (typeof equip === 'string' && equip.startsWith('[補強]')) {
        const eqName = equip.substring(4);
        name = eqName === '空' ? '空' : eqName;
        isExpansion = true;
    } else if (typeof equip === 'string') {
        name = equip;
    } else {
        name = '-';
    }

    const scrollThreshold = 4;
    const scrollClass = name.length > scrollThreshold ? 'equip-slot-scroll scroll' : 'equip-slot-scroll';
    const expansionClass = isExpansion ? 'equip-slot-expansion' : '';
    return `<div class="equip-slot-container"><span class="${scrollClass} ${expansionClass}" data-name="${name}">${name}</span></div>`;
}

//============================================================================================
// 処理ブロック名 : updateMaterialVal
//============================================================================================
function updateMaterialVal(elementId, newValue) {
    const el = document.getElementById(elementId);
    if (!el) return;

    const newText = newValue.toLocaleString();
    const oldText = el.textContent;

    if (oldText !== newText) {
        const oldVal = parseInt(oldText.replace(/,/g, ''), 10) || 0;

        el.textContent = newText;

        const parentItem = el.closest('.res-hud-item');
        if (parentItem) {
            parentItem.classList.remove('res-flash-up', 'res-flash-down');

            if (oldText && oldText !== '000,000' && oldText !== '0,000' && oldText !== '0') {
                void parentItem.offsetWidth;

                const flashClass = newValue > oldVal ? 'res-flash-up' : 'res-flash-down';
                parentItem.classList.add(flashClass);

                parentItem.addEventListener('animationend', () => {
                    parentItem.classList.remove(flashClass);
                }, { once: true });
            }
        }
    }
}

//============================================================================================
// 処理ブロック名 : updateHud
//============================================================================================
async function updateHud() {
    try {
        const params = `materialVer=${hudVersions.material}&fleetsVer=${hudVersions.fleets}&battleVer=${hudVersions.battle}&combatVer=${hudVersions.combat}&questsVer=${hudVersions.quests}&logsVer=${hudVersions.logs}`;
        const response = await fetch(`/hud?${params}&t=${Date.now()}`);

        if (response.status === 401) {
            console.warn("HUD: Session expired. Redirecting to login...");
            window.location.href = '/login-page';
            return;
        }

        if (response.status === 204) {
            if (typeof updateDataUsage === 'function') {
                updateDataUsage(getResponseHeaderBytes(response));
            }
            return;
        }

        if (!response.ok) throw new Error("HUD fetch failed");
        const text = await response.text();
        if (typeof updateDataUsage === 'function') {
            updateDataUsage(text.length + getResponseHeaderBytes(response));
        }
        const data = JSON.parse(text);

        // 1. 資源・資材
        if (data.material && data.material.changed) {
            hudVersions.material = data.material.version;
            const mat = data.material.data;
            if (mat) {
                updateMaterialVal('res-fuel-val', mat.fuel);
                updateMaterialVal('res-ammo-val', mat.ammo);
                updateMaterialVal('res-steel-val', mat.steel);
                updateMaterialVal('res-baux-val', mat.bauxite);
                updateMaterialVal('res-bucket-val', mat.bucket);
            }
        }

        // 2. 艦隊ステータス
        if (data.fleets && data.fleets.changed) {
            hudVersions.fleets = data.fleets.version;
            const fleetsData = data.fleets.data;

            if (fleetsData && fleetsData.length > 0) {
                window._fleetData = fleetsData;

                const fleetHud = document.getElementById('fleet-hud');
                fleetHud.innerHTML = '';

                fleetsData.forEach(fleet => {
                    const item = document.createElement('div');
                    let stateClass = 'fleet-state-ready';

                    if (fleet.state === 'MISSION') stateClass = 'fleet-state-expedition';
                    else if (fleet.state === 'DOCKING') stateClass = 'fleet-state-docking';
                    else if (fleet.state === 'STANDBY') stateClass = 'fleet-state-warning';
                    else if (fleet.state === 'EMERGENCY') stateClass = 'fleet-state-critical';

                    item.className = `fleet-hud-item ${stateClass}`;

                    if (fleet.id === currentDetailFleetId) {
                        item.classList.add('active');
                    }

                    const numSpan = document.createElement('span');
                    numSpan.className = 'fleet-num';
                    numSpan.textContent = fleet.id.toString().padStart(2, '0');

                    const statusSpan = document.createElement('span');
                    statusSpan.className = 'fleet-status';
                    statusSpan.textContent = fleet.stateText || (fleet.state === 'MISSION' ? 'MISSION' : 'READY');

                    item.appendChild(numSpan);
                    item.appendChild(statusSpan);
                    fleetHud.appendChild(item);
                });

                const label = document.createElement('span');
                label.className = 'fleet-hud-label';
                label.textContent = 'Tap 2 Toggle';
                fleetHud.appendChild(label);

                renderFleetDetail(currentDetailFleetId);
            }
        }

        // 3. 作戦海域・戦闘予測
        if (data.battle && data.battle.changed) {
            hudVersions.battle = data.battle.version;
            const bat = data.battle.data;
            if (bat) {
                document.getElementById('battle-area').textContent = bat.area;
                document.getElementById('battle-enemy').textContent = bat.enemy;
                document.getElementById('battle-predict').textContent = bat.predict;
            }
        }

        // 4. 詳細戦闘情報
        if (data.combat && data.combat.changed) {
            hudVersions.combat = data.combat.version;
            const com = data.combat.data;
            if (com) {
                document.getElementById('combat-formation').textContent = `FORM: ${com.formation || '------'}`;
                document.getElementById('combat-engagement').textContent = `ENGAGE: ${com.engagement || '------'}`;
                document.getElementById('combat-touch').textContent = `TOUCH: ${com.touch || '------'}`;

                const damageEl = document.getElementById('combat-damage');
                if (com.damageRate !== undefined && com.damageRate !== null) {
                    damageEl.textContent = `DMG: ${com.damageRate}%`;
                    damageEl.style.display = '';
                } else {
                    damageEl.style.display = 'none';
                }

                const victoryEl = document.getElementById('combat-victory');
                if (com.victory !== undefined && com.victory !== null && com.victory !== '') {
                    victoryEl.textContent = `RESULT: ${com.victory}`;
                    if (com.victory.includes('敗') || com.victory.includes('T-L') || com.victory.includes('B-L')) {
                        victoryEl.classList.add('defeat');
                    } else {
                        victoryEl.classList.remove('defeat');
                    }
                    victoryEl.style.display = '';
                } else {
                    victoryEl.style.display = 'none';
                }

                const airEl = document.getElementById('combat-air');
                if (com.airBattle !== undefined && com.airBattle !== null && com.airBattle !== '') {
                    airEl.textContent = `AIR: ${com.airBattle}`;
                    airEl.style.display = '';
                } else {
                    airEl.style.display = 'none';
                }

                document.getElementById('battle-predict').textContent = com.predict || 'NO PREDICT';

                const enemyText = com.enemyDesc || 'NO SIGNAL';
                document.getElementById('battle-enemy').textContent = `ENEMY: ${enemyText}`;

                const container = document.getElementById('combat-fleets-container');
                container.innerHTML = '';

                function createCombatFleetTable(title, ships, isEnemy) {
                    if (!ships || ships.length === 0) return null;

                    const section = document.createElement('div');
                    section.className = 'combat-fleet-section';

                    const titleEl = document.createElement('div');
                    titleEl.className = `combat-fleet-title${isEnemy ? ' enemy' : ''}`;
                    titleEl.textContent = title;
                    section.appendChild(titleEl);

                    const table = document.createElement('table');
                    table.className = 'combat-ship-table';

                    const thead = document.createElement('thead');
                    thead.innerHTML = '<tr><th>NAME</th><th>HP</th><th>ST</th></tr>';
                    table.appendChild(thead);

                    const tbody = document.createElement('tbody');
                    ships.forEach(ship => {
                        const tr = document.createElement('tr');

                        let statusClass = 'status-ok';
                        let statusText = 'OK';
                        if (ship.status === 'WARNING') { statusClass = 'status-warning'; statusText = 'WARN'; }
                        else if (ship.status === 'EMERGENCY') { statusClass = 'status-emergency'; statusText = 'BREAK'; }
                        else if (ship.status === 'SUNK') { statusClass = 'status-sunk'; statusText = 'SUNK'; }

                        const shipName = ship.name || '---';
                        const nameClass = shipName.length > 3 ? 'combat-ship-name-scroll scroll' : 'combat-ship-name-scroll';

                        const hpVal = ship.hp ?? '-';
                        const maxHpVal = ship.maxHp ?? '-';
                        const hpText = `${hpVal}/${maxHpVal}`;

                        let gaugeHtml = '';
                        if (hpVal !== '-' && maxHpVal !== '-' && parseInt(maxHpVal) > 0) {
                            const hpRate = Math.max(0, Math.min(1, parseInt(hpVal) / parseInt(maxHpVal)));
                            const hpSegments = Math.round(hpRate * 10);

                            let barClass = '';
                            if (statusClass === 'status-emergency') {
                                barClass = 'critical';
                            } else if (statusClass === 'status-warning') {
                                barClass = 'warning';
                            }

                            gaugeHtml = '<div class="combat-segment-gauge">';
                            for (let i = 1; i <= 10; i++) {
                                const segClass = i <= hpSegments
                                    ? `gauge-segment active ${barClass}`.trim()
                                    : 'gauge-segment';
                                gaugeHtml += `<span class="${segClass}"></span>`;
                            }
                            gaugeHtml += '</div>';
                        } else {
                            gaugeHtml = '<div class="combat-segment-gauge">';
                            for (let i = 1; i <= 10; i++) {
                                gaugeHtml += '<span class="gauge-segment sunk"></span>';
                            }
                            gaugeHtml += '</div>';
                        }

                        tr.innerHTML = `
                            <td class="combat-ship-name-cell">
                                <div class="combat-ship-name-container" title="${shipName}">
                                    <span class="${nameClass}" data-name="${shipName}">${shipName}</span>
                                </div>
                            </td>
                            <td class="combat-ship-hp-cell">
                                <div class="combat-cell-container">
                                    <span class="combat-hp-text">${hpText}</span>
                                    ${gaugeHtml}
                                </div>
                            </td>
                            <td class="combat-ship-status ${statusClass}">${statusText}</td>
                        `;
                        tbody.appendChild(tr);
                    });
                    table.appendChild(tbody);
                    section.appendChild(table);
                    return section;
                }

                const friendMain = createCombatFleetTable('FRIEND MAIN', com.friendShips, false);
                if (friendMain) container.appendChild(friendMain);

                const friendSub = createCombatFleetTable('FRIEND SUB', com.friendEscortShips, false);
                if (friendSub) container.appendChild(friendSub);

                const enemyMain = createCombatFleetTable('ENEMY MAIN', com.enemyShips, true);
                if (enemyMain) container.appendChild(enemyMain);

                const enemySub = createCombatFleetTable('ENEMY SUB', com.enemyEscortShips, true);
                if (enemySub) container.appendChild(enemySub);
            }
        }

        // 5. 任務データ
        const questStateKey = (data.quests ? data.quests.version : 0) + "_" + window._showQuestProgress;

        if (data.quests && (data.quests.changed || window._lastQuestsKey !== questStateKey)) {
            window._lastQuestsKey = questStateKey;
            hudVersions.quests = data.quests.version;
            const qData = data.quests.data;

            if (qData) {
                const questListDiv = document.getElementById('quest-list');
                if (questListDiv) {
                    questListDiv.innerHTML = '';

                    const maxSlots = 8;
                    for (let i = 0; i < maxSlots; i++) {
                        const quest = qData[i];
                        const row = document.createElement('div');
                        row.className = 'quest-slot-row';

                        if (quest && quest.progressRate >= 1.0) {
                            row.classList.add('quest-mission-ready');
                        }

                        if (quest) {
                            const qIdSpan = document.createElement('span');
                            qIdSpan.className = 'quest-id';
                            const formattedId = String(quest.id).padStart(4, '0');
                            qIdSpan.textContent = `[${formattedId}]`;

                            const container = document.createElement('div');
                            container.className = 'quest-text-container';

                            const namePanel = document.createElement('div');
                            namePanel.className = 'quest-view-panel quest-name-panel';

                            const scrollSpan = document.createElement('span');
                            const nameVal = quest.name || '---';
                            scrollSpan.textContent = nameVal;
                            scrollSpan.setAttribute('data-text', nameVal);

                            if (nameVal.length > 8) {
                                scrollSpan.className = 'quest-text-scroll scroll';
                            } else {
                                scrollSpan.className = 'quest-text-scroll';
                            }
                            namePanel.appendChild(scrollSpan);

                            const progressPanel = document.createElement('div');
                            progressPanel.className = 'quest-view-panel quest-progress-panel';

                            const progTextSpan = document.createElement('span');
                            progTextSpan.className = 'quest-progress-text';
                            const percentText = `${Math.round(quest.progressRate * 100)}%`;
                            progTextSpan.textContent = `${quest.progressText} (${percentText})`;

                            const barContainer = document.createElement('div');
                            barContainer.className = 'quest-progress-segment-gauge';

                            const questSegments = Math.round(quest.progressRate * 10);
                            for (let j = 1; j <= 10; j++) {
                                const segment = document.createElement('span');
                                segment.className = 'gauge-segment' + (j <= questSegments ? ' active' : '');
                                barContainer.appendChild(segment);
                            }

                            progressPanel.appendChild(progTextSpan);
                            progressPanel.appendChild(barContainer);

                            container.appendChild(namePanel);
                            container.appendChild(progressPanel);
                            row.appendChild(qIdSpan);
                            row.appendChild(container);

                            if (window._showQuestProgress) {
                                row.classList.add('show-progress');
                            }
                        } else {
                            row.classList.add('empty-quest-row');

                            const qIdSpan = document.createElement('span');
                            qIdSpan.className = 'quest-id';
                            qIdSpan.style.color = 'rgba(255, 102, 0, 0.3)';
                            qIdSpan.textContent = '[----]';

                            const container = document.createElement('div');
                            container.className = 'quest-text-container';

                            const scrollSpan = document.createElement('span');
                            scrollSpan.className = 'quest-text-scroll';
                            scrollSpan.style.color = 'rgba(163, 247, 191, 0.25)';
                            scrollSpan.textContent = 'NO QUEST ACTIVE';

                            container.appendChild(scrollSpan);
                            row.appendChild(qIdSpan);
                            row.appendChild(container);
                        }

                        questListDiv.appendChild(row);
                    }
                }
            }
        }

        // 6. ログデータ
        if (data.logs && data.logs.changed) {
            hudVersions.logs = data.logs.version;
            const logData = data.logs.data;
            if (logData) {
                const logListDiv = document.getElementById('log-list');
                if (logListDiv) {
                    logListDiv.innerHTML = '';
                    logData.forEach(log => {
                        const entry = document.createElement('div');
                        entry.className = `log-entry priority-${log.priority}`;
                        const pClass = `p${log.priority}`;
                        const escapedMessage = log.message.replace(/&/g, '&').replace(/</g, '<').replace(/>/g, '>').replace(/"/g, '"');
                        entry.innerHTML = `
                            <span class="log-time">${log.time}</span>
                            <span class="log-priority ${pClass}">P${log.priority}</span>
                            <span class="log-message" title="${escapedMessage}">${escapedMessage}</span>
                        `;
                        logListDiv.appendChild(entry);
                    });
                }
            }
        }
    } catch (error) {
        console.error("HUD synchronization error:", error);
    }
    setTimeout(updateHud, 2000);
}

//============================================================================================
// 処理ブロック名 : updateHudData
// 概要           : WebSocket経由で受信した初期HUDデータを反映する
//                  サーバーから {"type":"init_hud","data":{...}} 形式で受信
//                  2026/07/31 myi 新規作成（RemoteControllerBlackScreen修正）
//============================================================================================
function updateHudData(data) {
    try {
        // 1. 資源・資材
        if (data.material) {
            const mat = data.material;
            if (typeof mat.fuel !== 'undefined') updateMaterialVal('res-fuel-val', mat.fuel);
            if (typeof mat.ammo !== 'undefined') updateMaterialVal('res-ammo-val', mat.ammo);
            if (typeof mat.steel !== 'undefined') updateMaterialVal('res-steel-val', mat.steel);
            if (typeof mat.bauxite !== 'undefined') updateMaterialVal('res-baux-val', mat.bauxite);
            if (typeof mat.bucket !== 'undefined') updateMaterialVal('res-bucket-val', mat.bucket);
        }

        // 2. 艦隊データ
        if (data.fleets && Array.isArray(data.fleets)) {
            window._fleetData = data.fleets;
            renderFleetHud();
        }

        // 3. バトルデータ
        if (data.battle) {
            window._battleData = data.battle;
            renderBattleHud(data.battle);
        }

        // 4. 戦闘データ
        if (data.combat) {
            window._combatData = data.combat;
            renderCombatHud(data.combat);
        }

        // 5. クエストデータ
        if (data.quests) {
            window._questData = data.quests;
            renderQuestHud(data.quests);
        }

        // 6. ログデータ
        if (data.logs) {
            window._logData = data.logs;
            renderLogHud(data.logs);
        }

        console.log('[HUD] HUD data updated via WebSocket');
    } catch (error) {
        console.error('[HUD] updateHudData error:', error);
    }
}

//============================================================================================
// 処理ブロック名 : renderFleetHud
// 概要           : window._fleetData を基に艦隊ステータスHUDをレンダリングする
//                  2026/07/31 myi 新規作成（RemoteControllerBlackScreen修正）
//============================================================================================
function renderFleetHud() {
    const fleetHud = document.getElementById('fleet-hud');
    if (!fleetHud) return;

    const fleets = window._fleetData;
    if (!fleets || fleets.length === 0) return;

    fleetHud.innerHTML = '';

    fleets.forEach(fleet => {
        const item = document.createElement('div');
        let stateClass = 'fleet-state-ready';

        if (fleet.state === 'MISSION') stateClass = 'fleet-state-expedition';
        else if (fleet.state === 'DOCKING') stateClass = 'fleet-state-docking';
        else if (fleet.state === 'STANDBY') stateClass = 'fleet-state-warning';
        else if (fleet.state === 'EMERGENCY') stateClass = 'fleet-state-critical';

        item.className = `fleet-hud-item ${stateClass}`;

        if (fleet.id === currentDetailFleetId) {
            item.classList.add('active');
        }

        item.innerHTML = `
            <div class="fleet-num">${fleet.id}</div>
            <div class="fleet-name">${fleet.name || '---'}</div>
            <div class="fleet-state ${stateClass}">${fleet.state || 'READY'}</div>
        `;

        fleetHud.appendChild(item);
    });
}

//============================================================================================
// 処理ブロック名 : setElText
// 概要           : 要素のtextContentを安全に設定する共通ヘルパー
//                  要素が存在し、値がtruthyな場合のみ設定する
//                  2026/07/31 myi 新規作成（renderBattleHud/renderCombatHudの重複排除）
//============================================================================================
function setElText(id, value) {
    if (value) {
        const el = document.getElementById(id);
        if (el) el.textContent = value;
    }
}

//============================================================================================
// 処理ブロック名 : renderBattleHud
// 概要           : battleデータを基に作戦海域・戦闘予測HUDをレンダリングする
//                  2026/07/31 myi 新規作成（二次コードレビュー指摘修正）
//============================================================================================
function renderBattleHud(bat) {
    try {
        if (!bat) return;
        setElText('battle-area', bat.area);
        setElText('battle-enemy', bat.enemy);
        setElText('battle-predict', bat.predict);
    } catch (error) {
        console.error('[HUD] renderBattleHud error:', error);
    }
}

//============================================================================================
// 処理ブロック名 : renderCombatHud
// 概要           : combatデータを基に詳細戦闘情報HUDをレンダリングする
//                  2026/07/31 myi 新規作成（二次コードレビュー指摘修正）
//============================================================================================
function renderCombatHud(com) {
    try {
        if (!com) return;

        // 基本情報（フォーマット付き）
        const setFormatText = (id, prefix, value, fallback) => {
            const el = document.getElementById(id);
            if (el) el.textContent = `${prefix} ${value || fallback}`;
        };
        setFormatText('combat-formation', 'FORM', com.formation, '------');
        setFormatText('combat-engagement', 'ENGAGE', com.engagement, '------');
        setFormatText('combat-touch', 'TOUCH', com.touch, '------');

        // 条件付き表示の共通ヘルパー
        const setConditionalDisplay = (id, value, formatter) => {
            const el = document.getElementById(id);
            if (!el) return;
            if (value !== undefined && value !== null && value !== '') {
                el.textContent = formatter(value);
                el.style.display = '';
            } else {
                el.style.display = 'none';
            }
        };

        setConditionalDisplay('combat-damage', com.damageRate, v => `DMG: ${v}%`);

        const victoryEl = document.getElementById('combat-victory');
        if (victoryEl && com.victory !== undefined && com.victory !== null && com.victory !== '') {
            victoryEl.textContent = `RESULT: ${com.victory}`;
            if (com.victory.includes('敗') || com.victory.includes('T-L') || com.victory.includes('B-L')) {
                victoryEl.classList.add('defeat');
            } else {
                victoryEl.classList.remove('defeat');
            }
            victoryEl.style.display = '';
        } else if (victoryEl) {
            victoryEl.style.display = 'none';
        }

        setConditionalDisplay('combat-air', com.airBattle, v => `AIR: ${v}`);

        // 共有要素（battle-predict, battle-enemy）
        setElText('battle-predict', com.predict || 'NO PREDICT');
        setElText('battle-enemy', `ENEMY: ${com.enemyDesc || 'NO SIGNAL'}`);
    } catch (error) {
        console.error('[HUD] renderCombatHud error:', error);
    }
}

//============================================================================================
// 処理ブロック名 : renderQuestHud
// 概要           : questsデータを基に任務HUDをレンダリングする
//                  2026/07/31 myi 新規作成（二次コードレビュー指摘修正）
//============================================================================================
function renderQuestHud(qData) {
    try {
        if (!qData) return;
        const questListDiv = document.getElementById('quest-list');
        if (!questListDiv) return;

        questListDiv.innerHTML = '';
        const maxSlots = 8;

        for (let i = 0; i < maxSlots; i++) {
            const quest = qData[i];
            const row = document.createElement('div');
            row.className = 'quest-slot-row';

            if (quest && quest.progressRate >= 1.0) {
                row.classList.add('quest-mission-ready');
            }

            if (quest) {
                const qIdSpan = document.createElement('span');
                qIdSpan.className = 'quest-id';
                const formattedId = String(quest.id).padStart(4, '0');
                qIdSpan.textContent = `[${formattedId}]`;

                const scrollSpan = document.createElement('span');
                scrollSpan.className = 'quest-text-scroll';
                const nameVal = quest.name || '---';
                scrollSpan.textContent = nameVal;

                const progTextSpan = document.createElement('span');
                progTextSpan.className = 'quest-progress-text';
                const percentText = `${Math.round(quest.progressRate * 100)}%`;
                progTextSpan.textContent = `${quest.progressText} (${percentText})`;

                const container = document.createElement('div');
                container.className = 'quest-text-container';
                container.appendChild(scrollSpan);

                row.appendChild(qIdSpan);
                row.appendChild(container);

                if (window._showQuestProgress) {
                    row.classList.add('show-progress');
                }
            } else {
                row.classList.add('empty-quest-row');

                const qIdSpan = document.createElement('span');
                qIdSpan.className = 'quest-id';
                qIdSpan.style.color = 'rgba(255, 102, 0, 0.3)';
                qIdSpan.textContent = '[----]';

                const scrollSpan = document.createElement('span');
                scrollSpan.className = 'quest-text-scroll';
                scrollSpan.style.color = 'rgba(163, 247, 191, 0.25)';
                scrollSpan.textContent = 'NO QUEST ACTIVE';

                const container = document.createElement('div');
                container.className = 'quest-text-container';
                container.appendChild(scrollSpan);

                row.appendChild(qIdSpan);
                row.appendChild(container);
            }

            questListDiv.appendChild(row);
        }
    } catch (error) {
        console.error('[HUD] renderQuestHud error:', error);
    }
}

//============================================================================================
// 処理ブロック名 : renderLogHud
// 概要           : logsデータを基にログHUDをレンダリングする
//                  2026/07/31 myi 新規作成（二次コードレビュー指摘修正）
//============================================================================================
function renderLogHud(logData) {
    try {
        if (!logData) return;
        const logListDiv = document.getElementById('log-list');
        if (!logListDiv) return;

        logListDiv.innerHTML = '';
        logData.forEach(log => {
            const entry = document.createElement('div');
            entry.className = `log-entry priority-${log.priority}`;
            const pClass = `p${log.priority}`;
            const escapedMessage = log.message.replace(/&/g, '&').replace(/</g, '<').replace(/>/g, '>').replace(/"/g, '"');
            entry.innerHTML = `
                <span class="log-time">${log.time}</span>
                <span class="log-priority ${pClass}">P${log.priority}</span>
                <span class="log-message" title="${escapedMessage}">${escapedMessage}</span>
            `;
            logListDiv.appendChild(entry);
        });
    } catch (error) {
        console.error('[HUD] renderLogHud error:', error);
    }
}
