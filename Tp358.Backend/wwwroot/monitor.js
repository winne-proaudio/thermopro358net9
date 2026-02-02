const sensors = new Map();

        let deviceNames = {};

        function normalizeMac(mac) {
            if (!mac) {
                return '';
            }
            return mac.toString().trim().toUpperCase();
        }

        function getDeviceName(mac) {
            const key = normalizeMac(mac);
            return deviceNames[key] || "Unbekanntes Gerät";
        }

        async function loadDeviceNames() {
            try {
                const response = await fetch('/config/devices');
                if (!response.ok) {
                    throw new Error('Device mapping konnte nicht geladen werden');
                }
                const data = await response.json();
                const normalized = {};
                if (data && typeof data === 'object') {
                    Object.entries(data).forEach(([key, value]) => {
                        const normalizedKey = normalizeMac(key);
                        if (normalizedKey) {
                            normalized[normalizedKey] = value;
                        }
                    });
                }
                deviceNames = normalized;
                updateDisplay();
                if (currentView === 'graph') {
                    renderGraphs(latestGraphSeries, latestExternalSeries, lastGraphError);
                } else if (currentView === 'flow') {
                    renderFlowGraph(latestExternalSeries, lastExternalError);
                } else if (currentView === 'monitor') {
                    renderMonitorGraph(latestExternalSeries, lastExternalError);
                }
            } catch (error) {
                console.warn('Device mapping konnte nicht geladen werden:', error);
            }
        }

        function getGraphDeviceOrder(seriesByDevice) {
            const macs = new Set();
            if (seriesByDevice) {
                seriesByDevice.forEach((_, mac) => macs.add(mac));
            }
            if (macs.size === 0) {
                return Object.keys(deviceNames);
            }
            return Array.from(macs).sort((a, b) => {
                const nameA = getDeviceName(a);
                const nameB = getDeviceName(b);
                const nameCompare = nameA.localeCompare(nameB, 'de');
                if (nameCompare !== 0) {
                    return nameCompare;
                }
                return a.localeCompare(b);
            });
        }

        const statusIndicator = document.querySelector('.status-indicator');
        const connectionStatus = document.getElementById('connectionStatus');

        function setConnectionState(state, message) {
            statusIndicator.classList.remove('is-ok', 'is-warn', 'is-error');
            if (state === 'ok') {
                statusIndicator.classList.add('is-ok');
            } else if (state === 'warn') {
                statusIndicator.classList.add('is-warn');
            } else if (state === 'error') {
                statusIndicator.classList.add('is-error');
            }
            if (message) {
                statusIndicator.title = message;
                connectionStatus.title = message;
            } else {
                statusIndicator.removeAttribute('title');
                connectionStatus.removeAttribute('title');
            }
        }

        setConnectionState('warn', 'Verbinde mit Backend...');

        const homeViewButton = document.getElementById('homeViewButton');
        const graphViewButton = document.getElementById('graphViewButton');
        const flowViewButton = document.getElementById('flowViewButton');
        const monitorViewButton = document.getElementById('monitorViewButton');
        const graphControls = document.getElementById('graphControls');
        const flowControls = document.getElementById('flowControls');
        const timeButtons = Array.from(document.querySelectorAll('#graphControls .time-btn'));
        const smoothButtons = Array.from(document.querySelectorAll('#graphControls .smooth-btn'));
        const smoothMinus = document.getElementById('smoothMinus');
        const smoothPlus = document.getElementById('smoothPlus');
        const smoothMinutesLabel = document.getElementById('smoothMinutesLabel');
        const flowTimeButtons = Array.from(document.querySelectorAll('#flowControls .flow-time-btn'));
        const flowSmoothButtons = Array.from(document.querySelectorAll('#flowControls .flow-smooth-btn'));
        const flowSmoothMinus = document.getElementById('flowSmoothMinus');
        const flowSmoothPlus = document.getElementById('flowSmoothPlus');
        const flowSmoothMinutesLabel = document.getElementById('flowSmoothMinutesLabel');
        const homeView = document.getElementById('homeView');
        const graphView = document.getElementById('graphView');
        const flowView = document.getElementById('flowView');
        const graphContainer = document.getElementById('graphContainer');
        const graphNoData = document.getElementById('graphNoData');
        const flowContainer = document.getElementById('flowContainer');
        const flowNoData = document.getElementById('flowNoData');
        const monitorView = document.getElementById('monitorView');
        const monitorContainer = document.getElementById('monitorContainer');
        const monitorNoData = document.getElementById('monitorNoData');
        const monitorMinutesMinus = document.getElementById('monitorMinutesMinus');
        const monitorMinutesPlus = document.getElementById('monitorMinutesPlus');
        const monitorMinutesLabel = document.getElementById('monitorMinutesLabel');
        const monitorSmoothToggle = document.getElementById('monitorSmoothToggle');

        let currentView = 'home';
        let graphRefreshTimer = null;
        let latestGraphSeries = new Map();
        let latestExternalSeries = new Map();
        let smoothingMode = 'A';
        let smoothingWindowMinutes = 2;
        let flowSmoothingMode = 'A';
        let flowSmoothingWindowMinutes = 2;
        let flowHours = 24;
        let currentGraphRefreshIntervalMs = graphRefreshIntervalMs;
        let monitorWindowMinutes = monitorGraph.windowMinutes;
        const monitorStepMinutes = 5;
        let monitorSmoothingEnabled = false;
        let monitorSmoothingWindowMinutes = 5;
        let lastGraphError = false;
        let lastExternalError = false;

        function setView(view) {
            currentView = view;
            const isHome = view === 'home';
            const isGraph = view === 'graph';
            const isFlow = view === 'flow';
            const isMonitor = view === 'monitor';
            homeView.style.display = isHome ? 'block' : 'none';
            graphView.style.display = isGraph ? 'flex' : 'none';
            flowView.style.display = isFlow ? 'flex' : 'none';
            monitorView.style.display = isMonitor ? 'flex' : 'none';
            homeViewButton.classList.toggle('active', isHome);
            graphViewButton.classList.toggle('active', isGraph);
            flowViewButton.classList.toggle('active', isFlow);
            monitorViewButton.classList.toggle('active', isMonitor);
            graphControls.style.display = isGraph ? 'flex' : 'none';
            flowControls.style.display = isFlow ? 'flex' : 'none';
            timeButtons.forEach(btn => {
                btn.disabled = isHome || isFlow || isMonitor;
            });
            smoothButtons.forEach(btn => {
                btn.disabled = isHome || isFlow || isMonitor;
            });
            smoothMinus.disabled = isHome || isFlow || isMonitor;
            smoothPlus.disabled = isHome || isFlow || isMonitor;
            flowTimeButtons.forEach(btn => {
                btn.disabled = !isFlow;
            });
            flowSmoothButtons.forEach(btn => {
                btn.disabled = !isFlow;
            });
            flowSmoothMinus.disabled = !isFlow;
            flowSmoothPlus.disabled = !isFlow;

            if (isHome) {
                stopGraphRefresh();
            } else {
                loadGraphData();
                startGraphRefresh(isMonitor ? monitorGraph.refreshIntervalMs : graphRefreshIntervalMs);
            }
        }

        homeViewButton.addEventListener('click', () => setView('home'));
        graphViewButton.addEventListener('click', () => setView('graph'));
        flowViewButton.addEventListener('click', () => setView('flow'));
        monitorViewButton.addEventListener('click', () => setView('monitor'));
        timeButtons.forEach(button => {
            button.addEventListener('click', () => {
                const hours = Number.parseInt(button.dataset.hours ?? '24', 10);
                setTimeRange(Number.isFinite(hours) ? hours : 24);
            });
        });
        smoothButtons.forEach(button => {
            button.addEventListener('click', () => {
                const mode = button.dataset.smooth ?? 'A';
                setSmoothingMode(mode);
            });
        });
        smoothMinus.addEventListener('click', () => setSmoothingMinutes(smoothingWindowMinutes - 1));
        smoothPlus.addEventListener('click', () => setSmoothingMinutes(smoothingWindowMinutes + 1));
        flowTimeButtons.forEach(button => {
            button.addEventListener('click', () => {
                const hours = Number.parseInt(button.dataset.hours ?? '24', 10);
                setFlowTimeRange(Number.isFinite(hours) ? hours : 24);
            });
        });
        flowSmoothButtons.forEach(button => {
            button.addEventListener('click', () => {
                const mode = button.dataset.smooth ?? 'A';
                setFlowSmoothingMode(mode);
            });
        });
        flowSmoothMinus.addEventListener('click', () => setFlowSmoothingMinutes(flowSmoothingWindowMinutes - 1));
        flowSmoothPlus.addEventListener('click', () => setFlowSmoothingMinutes(flowSmoothingWindowMinutes + 1));
        monitorMinutesMinus.addEventListener('click', () => setMonitorWindowMinutes(monitorWindowMinutes - monitorStepMinutes));
        monitorMinutesPlus.addEventListener('click', () => setMonitorWindowMinutes(monitorWindowMinutes + monitorStepMinutes));
        monitorSmoothToggle.addEventListener('click', () => toggleMonitorSmoothing());

        function setTimeRange(hours) {
            graphConfig.hours = hours;
            timeButtons.forEach(btn => {
                btn.classList.toggle('active', Number.parseInt(btn.dataset.hours ?? '0', 10) === hours);
            });
            if (currentView === 'graph') {
                loadGraphData();
            }
        }

        function setSmoothingMode(mode) {
            smoothingMode = ['A', 'B'].includes(mode) ? mode : 'A';
            smoothButtons.forEach(btn => {
                btn.classList.toggle('active', (btn.dataset.smooth ?? 'A') === smoothingMode);
            });
            if (currentView === 'graph') {
                renderGraphs(latestGraphSeries, latestExternalSeries);
            }
        }

        function setSmoothingMinutes(minutes) {
            const next = Math.max(1, minutes);
            smoothingWindowMinutes = next;
            smoothMinutesLabel.textContent = `${next} min`;
            if (currentView === 'graph') {
                renderGraphs(latestGraphSeries, latestExternalSeries);
            }
        }

        function setFlowTimeRange(hours) {
            flowHours = hours;
            flowTimeButtons.forEach(btn => {
                btn.classList.toggle('active', Number.parseInt(btn.dataset.hours ?? '0', 10) === hours);
            });
            if (currentView === 'flow') {
                loadGraphData();
            }
        }

        function setFlowSmoothingMode(mode) {
            flowSmoothingMode = ['A', 'B'].includes(mode) ? mode : 'A';
            flowSmoothButtons.forEach(btn => {
                btn.classList.toggle('active', (btn.dataset.smooth ?? 'A') === flowSmoothingMode);
            });
            if (currentView === 'flow') {
                renderFlowGraph(latestExternalSeries, lastExternalError);
            }
        }

        function setFlowSmoothingMinutes(minutes) {
            const next = Math.max(1, minutes);
            flowSmoothingWindowMinutes = next;
            flowSmoothMinutesLabel.textContent = `${next} min`;
            if (currentView === 'flow') {
                renderFlowGraph(latestExternalSeries, lastExternalError);
            }
        }

        function setMonitorWindowMinutes(minutes) {
            const clamped = Math.min(120, Math.max(10, minutes));
            monitorWindowMinutes = clamped;
            monitorMinutesLabel.textContent = `${clamped} min`;
            if (currentView === 'monitor') {
                renderMonitorGraph(latestExternalSeries, lastExternalError);
            }
        }

        function toggleMonitorSmoothing() {
            monitorSmoothingEnabled = !monitorSmoothingEnabled;
            updateMonitorSmoothingUi();
            if (currentView === 'monitor') {
                renderMonitorGraph(latestExternalSeries, lastExternalError);
            }
        }

        function updateMonitorSmoothingUi() {
            monitorSmoothToggle.textContent = monitorSmoothingEnabled ? 'Glättung: An' : 'Glättung: Aus';
            monitorSmoothToggle.classList.toggle('active', monitorSmoothingEnabled);
        }

        updateMonitorSmoothingUi();
        loadDeviceNames();

        const shutdownButton = document.getElementById('shutdownButton');
        const shutdownButtonLabel = shutdownButton.textContent;

        function setShutdownEnabled(enabled) {
            shutdownButton.disabled = !enabled;
            if (!enabled) {
                shutdownButton.textContent = shutdownButtonLabel;
            }
        }

        async function requestShutdown() {
            const confirmed = window.confirm('Backend wirklich herunterfahren?');
            if (!confirmed) {
                return;
            }

            shutdownButton.disabled = true;
            shutdownButton.textContent = '⏻ Wird beendet...';

            try {
                const response = await fetch('/shutdown', { method: 'POST' });
                if (!response.ok) {
                    throw new Error('Shutdown fehlgeschlagen');
                }
                setConnectionState('warn', 'Backend wird beendet...');
            } catch (error) {
                shutdownButton.disabled = false;
                shutdownButton.textContent = shutdownButtonLabel;
                window.alert('Shutdown fehlgeschlagen. Bitte erneut versuchen.');
                console.error('Shutdown Error:', error);
            }
        }

        shutdownButton.addEventListener('click', requestShutdown);

        function startGraphRefresh(intervalMs = graphRefreshIntervalMs) {
            if (graphRefreshTimer && currentGraphRefreshIntervalMs === intervalMs) {
                return;
            }
            stopGraphRefresh();
            currentGraphRefreshIntervalMs = intervalMs;
            graphRefreshTimer = setInterval(loadGraphData, intervalMs);
        }

        function stopGraphRefresh() {
            if (!graphRefreshTimer) {
                return;
            }
            clearInterval(graphRefreshTimer);
            graphRefreshTimer = null;
        }

        async function loadGraphData() {
            const sensorPromise = fetch(`/measurements/temperature?hours=${graphConfig.hours}`);
            const externalHours = currentView === 'flow' ? flowHours : graphConfig.hours;
            const externalPromise = fetch(`/measurements/external?hours=${externalHours}`);
            const [sensorResult, externalResult] = await Promise.allSettled([sensorPromise, externalPromise]);

            let grouped = new Map();
            let externalSeries = new Map();
            let sensorError = false;
            let externalError = false;

            try {
                if (sensorResult.status === 'fulfilled') {
                    if (!sensorResult.value.ok) {
                        throw new Error('Sensor-Messwerte konnten nicht geladen werden');
                    }
                    const measurements = await sensorResult.value.json();
                    grouped = new Map();
                    measurements.forEach(item => {
                        if (!grouped.has(item.deviceMac)) {
                            grouped.set(item.deviceMac, []);
                        }
                        if (item.temperatureC === null || item.temperatureC === undefined) {
                            return;
                        }
                        grouped.get(item.deviceMac).push({
                            time: parseExternalTimestamp(item.measuredAt),
                            temp: item.temperatureC
                        });
                    });
                } else {
                    throw sensorResult.reason;
                }
            } catch (error) {
                sensorError = true;
                console.error('Graph Sensor Error:', error);
            }

            try {
                if (externalResult.status === 'fulfilled') {
                    if (!externalResult.value.ok) {
                        throw new Error('Externe Messwerte konnten nicht geladen werden');
                    }
                    const measurements = await externalResult.value.json();
                    externalSeries = new Map();
                    measurements.forEach(item => {
                        if (!item.deviceId) {
                            return;
                        }
                        if (item.temperature === null || item.temperature === undefined) {
                            return;
                        }
                        if (!externalSeries.has(item.deviceId)) {
                            externalSeries.set(item.deviceId, []);
                        }
                        externalSeries.get(item.deviceId).push({
                            time: parseExternalTimestamp(item.timestamp),
                            temp: item.temperature
                        });
                    });
                } else {
                    throw externalResult.reason;
                }
            } catch (error) {
                externalError = true;
                console.error('Graph External Error:', error);
            }

            latestGraphSeries = grouped;
            latestExternalSeries = externalSeries;
            lastGraphError = sensorError || externalError;
            lastExternalError = externalError;

            if (currentView === 'flow') {
                renderFlowGraph(externalSeries, externalError);
            } else if (currentView === 'monitor') {
                renderMonitorGraph(externalSeries, externalError);
            } else {
                renderGraphs(grouped, externalSeries, sensorError || externalError);
            }
        }

        function renderGraphs(seriesByDevice, externalSeries, hadError) {
            graphContainer.innerHTML = '';
            let hasData = false;
            const timeRange = getTimeRange(graphConfig.hours);

            const graphDeviceOrder = getGraphDeviceOrder(seriesByDevice);
            graphDeviceOrder.forEach((mac, index) => {
                const row = document.createElement('div');
                row.className = 'graph-row';

                const title = document.createElement('div');
                title.className = 'graph-title graph-title--sensor';
                const titleLeft = document.createElement('div');
                titleLeft.className = 'graph-title-left';
                titleLeft.textContent = `${getDeviceName(mac)} (${mac})`;

                const titleCenter = document.createElement('div');
                titleCenter.className = 'graph-title-center';

                const titleLatest = document.createElement('div');
                titleLatest.className = 'graph-title-latest';

                const canvas = document.createElement('canvas');
                canvas.className = 'graph-canvas';

                title.appendChild(titleLeft);
                title.appendChild(titleCenter);
                title.appendChild(titleLatest);
                row.appendChild(title);
                row.appendChild(canvas);
                graphContainer.appendChild(row);

                const points = (seriesByDevice.get(mac) || []).slice().sort((a, b) => a.time - b.time);
                const smoothedPoints = applySmoothing(points, smoothingMode, smoothingWindowMinutes);
                const visiblePoints = filterPointsByRange(smoothedPoints, timeRange);
                if (visiblePoints.length > 0) {
                    hasData = true;
                }
                const color = deviceColors[index % deviceColors.length];
                if (visiblePoints.length > 0) {
                    const temps = visiblePoints.map(point => point.temp);
                    const min = Math.min(...temps);
                    const max = Math.max(...temps);
                    const latest = visiblePoints[visiblePoints.length - 1].temp;
                    titleCenter.textContent = `Min: ${min.toFixed(1)} °C · Max: ${max.toFixed(1)} °C`;
                    titleLatest.innerHTML = `<span class="latest-item" style="color:${color}">${latest.toFixed(1)} °C</span>`;
                } else {
                    titleCenter.textContent = 'Min: n/a · Max: n/a';
                    titleLatest.innerHTML = '<span class="latest-item">n/a</span>';
                }
                drawGraph(canvas, visiblePoints, color, graphConfig.minTemp, graphConfig.maxTemp, timeRange, graphConfig.rowHeight);
            });

            const externalRow = document.createElement('div');
            externalRow.className = 'graph-row';

            const externalTitle = document.createElement('div');
            externalTitle.className = 'graph-title';

            const externalTitleLeft = document.createElement('div');
            externalTitleLeft.className = 'graph-title-left';
            externalTitleLeft.textContent = externalGraph.name;

            const externalTitleCenter = document.createElement('div');
            externalTitleCenter.className = 'graph-title-center';

            const externalTitleLatest = document.createElement('div');
            externalTitleLatest.className = 'graph-title-latest';

            const externalLegend = document.createElement('div');
            externalLegend.className = 'graph-legend';
            externalGraph.series.forEach(item => {
                const legendItem = document.createElement('div');
                const dot = document.createElement('span');
                dot.className = 'legend-dot';
                dot.style.background = item.color;
                legendItem.appendChild(dot);
                legendItem.appendChild(document.createTextNode(item.label));
                externalLegend.appendChild(legendItem);
            });

            const externalCanvas = document.createElement('canvas');
            externalCanvas.className = 'graph-canvas';

            externalRow.appendChild(externalTitle);
            externalRow.appendChild(externalLegend);
            externalRow.appendChild(externalCanvas);
            graphContainer.appendChild(externalRow);

            const externalSeriesList = externalGraph.series.map(item => {
                const points = (externalSeries.get(item.deviceId) || []).slice().sort((a, b) => a.time - b.time);
                const smoothedPoints = applySmoothing(points, smoothingMode, smoothingWindowMinutes);
                const visiblePoints = filterPointsByRange(smoothedPoints, timeRange);
                if (visiblePoints.length > 0) {
                    hasData = true;
                }
                return { label: item.label, color: item.color, points: visiblePoints };
            });
            const latestText = formatLatestTemps(externalSeriesList);
            const deltaText = formatDeltaText(externalSeriesList, externalGraph.delta);
            externalTitleCenter.textContent = deltaText;
            externalTitleLatest.innerHTML = latestText;
            externalTitle.appendChild(externalTitleLeft);
            externalTitle.appendChild(externalTitleCenter);
            externalTitle.appendChild(externalTitleLatest);
            drawGraphMulti(externalCanvas, externalSeriesList, externalGraph.minTemp, externalGraph.maxTemp, timeRange, graphConfig.rowHeight);

            graphNoData.style.display = hasData ? 'none' : 'block';
            graphNoData.textContent = hadError ? 'Fehler beim Laden der Messwerte.' : 'Keine Messwerte in der Datenbank gefunden.';
        }

        function renderFlowGraph(externalSeries, hadError) {
            flowContainer.innerHTML = '';
            let hasData = false;
            const timeRange = getTimeRange(flowHours);

            const row = document.createElement('div');
            row.className = 'graph-row';

            const title = document.createElement('div');
            title.className = 'graph-title';

            const titleLeft = document.createElement('div');
            titleLeft.className = 'graph-title-left';

            const titleCenter = document.createElement('div');
            titleCenter.className = 'graph-title-center';

            const titleLatest = document.createElement('div');
            titleLatest.className = 'graph-title-latest';

            const legend = document.createElement('div');
            legend.className = 'graph-legend';
            externalGraph.series.forEach(item => {
                const legendItem = document.createElement('div');
                const dot = document.createElement('span');
                dot.className = 'legend-dot';
                dot.style.background = item.color;
                legendItem.appendChild(dot);
                legendItem.appendChild(document.createTextNode(item.label));
                legend.appendChild(legendItem);
            });

            const canvas = document.createElement('canvas');
            canvas.className = 'graph-canvas';

            row.appendChild(title);
            row.appendChild(legend);
            row.appendChild(canvas);
            flowContainer.appendChild(row);

            const seriesList = externalGraph.series.map(item => {
                const points = (externalSeries.get(item.deviceId) || []).slice().sort((a, b) => a.time - b.time);
                const smoothedPoints = applySmoothing(points, flowSmoothingMode, flowSmoothingWindowMinutes);
                const visiblePoints = filterPointsByRange(smoothedPoints, timeRange);
                if (visiblePoints.length > 0) {
                    hasData = true;
                }
                return { label: item.label, color: item.color, points: visiblePoints };
            });

            const latestText = formatLatestTemps(seriesList);
            const deltaText = formatDeltaText(seriesList, externalGraph.delta);
            titleLeft.textContent = flowGraph.name;
            titleCenter.textContent = deltaText;
            titleLatest.innerHTML = latestText;
            title.appendChild(titleLeft);
            title.appendChild(titleCenter);
            title.appendChild(titleLatest);

            drawGraphMulti(canvas, seriesList, flowGraph.minTemp, flowGraph.maxTemp, timeRange, flowGraph.rowHeight);

            flowNoData.style.display = hasData ? 'none' : 'block';
            flowNoData.textContent = hadError ? 'Fehler beim Laden der Messwerte.' : 'Keine Messwerte in der Datenbank gefunden.';
        }

        function renderMonitorGraph(externalSeries, hadError) {
            monitorContainer.innerHTML = '';
            let hasData = false;
            const timeRange = getMonitorTimeRange(externalSeries, monitorWindowMinutes);

            const row = document.createElement('div');
            row.className = 'graph-row';

            const title = document.createElement('div');
            title.className = 'graph-title';

            const titleLeft = document.createElement('div');
            titleLeft.className = 'graph-title-left';

            const titleCenter = document.createElement('div');
            titleCenter.className = 'graph-title-center';

            const titleLatest = document.createElement('div');
            titleLatest.className = 'graph-title-latest';

            const legend = document.createElement('div');
            legend.className = 'graph-legend';
            externalGraph.series.forEach(item => {
                const legendItem = document.createElement('div');
                const dot = document.createElement('span');
                dot.className = 'legend-dot';
                dot.style.background = item.color;
                legendItem.appendChild(dot);
                legendItem.appendChild(document.createTextNode(item.label));
                legend.appendChild(legendItem);
            });

            const canvas = document.createElement('canvas');
            canvas.className = 'graph-canvas';

            row.appendChild(title);
            row.appendChild(legend);
            row.appendChild(canvas);
            monitorContainer.appendChild(row);

            const seriesList = externalGraph.series.map(item => {
                const points = (externalSeries.get(item.deviceId) || []).slice().sort((a, b) => a.time - b.time);
                const smoothedPoints = applySmoothing(points, flowSmoothingMode, monitorSmoothingWindowMinutes);
                const basePoints = monitorSmoothingEnabled ? smoothedPoints : points;
                const visiblePoints = filterPointsByRange(basePoints, timeRange);
                if (visiblePoints.length > 0) {
                    hasData = true;
                }
                const rawVisible = filterPointsByRange(points, timeRange);
                return { label: item.label, color: item.color, points: visiblePoints, rawPoints: rawVisible };
            });

            const latestText = formatLatestTemps(seriesList);
            const deltaText = formatDeltaText(seriesList, { ...externalGraph.delta, requireThreshold: false });
            titleLeft.textContent = monitorGraph.name;
            titleCenter.textContent = deltaText;
            titleLatest.innerHTML = latestText;
            title.appendChild(titleLeft);
            title.appendChild(titleCenter);
            title.appendChild(titleLatest);

            drawGraphMulti(canvas, seriesList, monitorGraph.minTemp, monitorGraph.maxTemp, timeRange, monitorGraph.rowHeight, !monitorSmoothingEnabled);

            monitorNoData.style.display = hasData ? 'none' : 'block';
            monitorNoData.textContent = hadError ? 'Fehler beim Laden der Messwerte.' : 'Keine Messwerte in der Datenbank gefunden.';
        }

        function getTimeRange(hours) {
            const effectiveHours = hours ?? graphConfig.hours;
            const now = new Date();
            const to = new Date(now);
            const needsCeil = now.getMinutes() !== 0 || now.getSeconds() !== 0 || now.getMilliseconds() !== 0;
            to.setMinutes(0, 0, 0);
            if (needsCeil) {
                to.setHours(to.getHours() + 1);
            }
            const from = new Date(to.getTime() - effectiveHours * 60 * 60 * 1000);
            return { from, to, totalMs: to - from, hours: effectiveHours };
        }

        function getTimeRangeMinutes(minutes) {
            const effectiveMinutes = minutes ?? 20;
            const now = new Date();
            const to = new Date(now);
            to.setSeconds(0, 0);
            const from = new Date(to.getTime() - effectiveMinutes * 60 * 1000);
            return { from, to, totalMs: to - from, minutes: effectiveMinutes };
        }

        function getMonitorTimeRange(seriesMap, minutes) {
            const rangeNow = getTimeRangeMinutes(minutes);
            const latestTime = getLatestSeriesTime(seriesMap);
            if (!latestTime) {
                return rangeNow;
            }

            const latestMs = latestTime.getTime();
            if (latestMs >= rangeNow.from.getTime() && latestMs <= rangeNow.to.getTime()) {
                return rangeNow;
            }

            return getTimeRangeMinutesFromTimestamp(latestTime, minutes);
        }

        function getLatestSeriesTime(seriesMap) {
            if (!seriesMap || !seriesMap.size) {
                return null;
            }
            let maxTime = null;
            seriesMap.forEach(points => {
                points.forEach(point => {
                    const t = point.time;
                    if (!maxTime || t.getTime() > maxTime.getTime()) {
                        maxTime = t;
                    }
                });
            });
            return maxTime;
        }

        function getTimeRangeMinutesFromTimestamp(timestamp, minutes) {
            const effectiveMinutes = minutes ?? 20;
            const base = new Date(timestamp);
            base.setSeconds(0, 0);
            const to = new Date(base);
            const from = new Date(to.getTime() - effectiveMinutes * 60 * 1000);
            return { from, to, totalMs: to - from, minutes: effectiveMinutes };
        }


        function drawGraph(canvas, points, color, minTemp, maxTemp, timeRange, rowHeight) {
            const dpr = window.devicePixelRatio || 1;
            const width = canvas.clientWidth || 800;
            const height = rowHeight ?? graphConfig.rowHeight;

            canvas.width = Math.floor(width * dpr);
            canvas.height = Math.floor(height * dpr);
            canvas.style.height = `${height}px`;

            const ctx = canvas.getContext('2d');
            ctx.scale(dpr, dpr);
            ctx.clearRect(0, 0, width, height);

            const frame = drawGraphFrame(ctx, width, height, minTemp, maxTemp, timeRange);

            if (points.length === 0) {
                ctx.fillStyle = '#9ca3af';
                ctx.fillText('Keine Daten', frame.left + 10, frame.top + 18);
                return;
            }

            ctx.strokeStyle = color;
            ctx.lineWidth = 2;
            ctx.beginPath();
            points.forEach((point, index) => {
                const clampedTemp = Math.min(maxTemp, Math.max(minTemp, point.temp));
                const x = frame.left + ((point.time - frame.from) / frame.totalMs) * frame.plotWidth;
                const y = frame.top + ((maxTemp - clampedTemp) / frame.range) * frame.plotHeight;
                if (index === 0) {
                    ctx.moveTo(x, y);
                } else {
                    ctx.lineTo(x, y);
                }
            });
            ctx.stroke();
        }

        function drawGraphMulti(canvas, seriesList, minTemp, maxTemp, timeRange, rowHeight, drawRawPoints) {
            const dpr = window.devicePixelRatio || 1;
            const width = canvas.clientWidth || 800;
            const height = rowHeight ?? graphConfig.rowHeight;

            canvas.width = Math.floor(width * dpr);
            canvas.height = Math.floor(height * dpr);
            canvas.style.height = `${height}px`;

            const ctx = canvas.getContext('2d');
            ctx.scale(dpr, dpr);
            ctx.clearRect(0, 0, width, height);

            const frame = drawGraphFrame(ctx, width, height, minTemp, maxTemp, timeRange);

            const hasData = seriesList.some(series => series.points.length > 0);
            if (!hasData) {
                ctx.fillStyle = '#9ca3af';
                ctx.fillText('Keine Daten', frame.left + 10, frame.top + 18);
                return;
            }

            seriesList.forEach(series => {
                if (series.points.length === 0) {
                    return;
                }
                ctx.strokeStyle = series.color;
                ctx.lineWidth = 2;
                ctx.beginPath();
                series.points.forEach((point, index) => {
                    const clampedTemp = Math.min(maxTemp, Math.max(minTemp, point.temp));
                    const x = frame.left + ((point.time - frame.from) / frame.totalMs) * frame.plotWidth;
                    const y = frame.top + ((maxTemp - clampedTemp) / frame.range) * frame.plotHeight;
                    if (index === 0) {
                        ctx.moveTo(x, y);
                    } else {
                        ctx.lineTo(x, y);
                    }
                });
                ctx.stroke();

                if (drawRawPoints && series.rawPoints && series.rawPoints.length > 0) {
                    ctx.fillStyle = series.color;
                    series.rawPoints.forEach(point => {
                        const clampedTemp = Math.min(maxTemp, Math.max(minTemp, point.temp));
                        const x = frame.left + ((point.time - frame.from) / frame.totalMs) * frame.plotWidth;
                        const y = frame.top + ((maxTemp - clampedTemp) / frame.range) * frame.plotHeight;
                        ctx.beginPath();
                        ctx.arc(x, y, 2, 0, Math.PI * 2);
                        ctx.fill();
                    });
                }
            });
        }

        function drawGraphFrame(ctx, width, height, minTemp, maxTemp, timeRange) {
            const { left, right, top, bottom } = graphConfig.padding;
            const plotWidth = width - left - right;
            const plotHeight = height - top - bottom;
            const range = maxTemp - minTemp;

            const from = timeRange.from;
            const totalMs = timeRange.totalMs;

            ctx.strokeStyle = '#e5e7eb';
            ctx.lineWidth = 1;
            ctx.font = '10px sans-serif';
            ctx.fillStyle = '#6b7280';

            for (let t = minTemp; t <= maxTemp; t += 1) {
                const y = top + ((maxTemp - t) / range) * plotHeight;
                ctx.beginPath();
                ctx.moveTo(left, y);
                ctx.lineTo(left + plotWidth, y);
                ctx.stroke();
                ctx.fillText(t.toString(), 8, y + 3);
            }

            const dashedLines = [
                { temp: 20, color: '#f59e0b' },
                { temp: 23, color: '#10b981' },
                { temp: 35, color: '#3b82f6' },
                { temp: 45, color: '#ef4444' }
            ];

            ctx.save();
            ctx.setLineDash([1, 6]);
            ctx.lineCap = 'round';
            dashedLines.forEach(line => {
                if (line.temp < minTemp || line.temp > maxTemp) {
                    return;
                }
                const y = top + ((maxTemp - line.temp) / range) * plotHeight;
                ctx.strokeStyle = line.color;
                ctx.lineWidth = 1;
                ctx.beginPath();
                ctx.moveTo(left, y);
                ctx.lineTo(left + plotWidth, y);
                ctx.stroke();
            });
            ctx.restore();
            ctx.strokeStyle = '#e5e7eb';
            ctx.lineWidth = 1;

            if (timeRange.minutes) {
                const totalMinutes = timeRange.minutes;
                const labelEveryMinutes = totalMinutes <= 30 ? 5 : 10;
                const stepMinutes = width < 600 ? labelEveryMinutes * 2 : labelEveryMinutes;
                for (let m = 0; m <= totalMinutes; m += stepMinutes) {
                    const x = left + (m / totalMinutes) * plotWidth;
                    ctx.beginPath();
                    ctx.moveTo(x, top);
                    ctx.lineTo(x, top + plotHeight);
                    ctx.stroke();

                    const tickTime = new Date(from.getTime() + m * 60 * 1000);
                    const label = tickTime.toLocaleTimeString('de-DE', { hour: '2-digit', minute: '2-digit' });
                    ctx.fillText(label, x - 14, top + plotHeight + 16);
                }
            } else {
                const hours = timeRange.hours ?? graphConfig.hours;
                const baseLabelEvery = Math.max(1, Math.round(hours / 12));
                const labelEvery = width < 600 ? baseLabelEvery * 2 : baseLabelEvery;
                for (let h = 0; h <= hours; h += 1) {
                    const x = left + (h / hours) * plotWidth;
                    ctx.beginPath();
                    ctx.moveTo(x, top);
                    ctx.lineTo(x, top + plotHeight);
                    ctx.stroke();

                    if (h % labelEvery === 0) {
                        const tickTime = new Date(from.getTime() + h * 60 * 60 * 1000);
                        const label = tickTime.toLocaleTimeString('de-DE', { hour: '2-digit', minute: '2-digit' });
                        ctx.fillText(label, x - 14, top + plotHeight + 16);
                    }
                }
            }

            ctx.strokeStyle = '#9ca3af';
            ctx.lineWidth = 1.5;
            ctx.beginPath();
            ctx.moveTo(left, top);
            ctx.lineTo(left, top + plotHeight);
            ctx.lineTo(left + plotWidth, top + plotHeight);
            ctx.stroke();

            return { left, top, plotWidth, plotHeight, range, from, totalMs };
        }

        function formatDeltaText(seriesList, deltaConfig) {
            const stats = computeDeltaStats(seriesList, deltaConfig);
            if (!stats) {
                return 'Delta T: n/a · Min: n/a · Max: n/a';
            }
            return `Delta T: ${stats.current.toFixed(1)} °C · Min: ${stats.min.toFixed(1)} °C · Max: ${stats.max.toFixed(1)} °C`;
        }

        function formatLatestTemps(seriesList) {
            return seriesList.map(series => {
                const color = series.color || '#6b7280';
                const label = series.label;
                if (!series.points || series.points.length === 0) {
                    return `<span class="latest-item" style="color:${color}">${label}: n/a</span>`;
                }
                const latest = series.points[series.points.length - 1].temp;
                return `<span class="latest-item" style="color:${color}">${label}: ${latest.toFixed(1)} °C</span>`;
            }).join('');
        }

        function computeDeltaStats(seriesList, deltaConfig) {
            const steig = seriesList.find(item => item.label === 'Steigleitung');
            const rueck = seriesList.find(item => item.label === 'Rücklauf');
            if (!steig || !rueck || steig.points.length === 0 || rueck.points.length === 0) {
                return null;
            }

            const threshold = deltaConfig?.startThreshold ?? 38;
            const requireThreshold = deltaConfig?.requireThreshold !== false;
            const matchMinutes = deltaConfig?.matchMinutes ?? 2;
            const matchMs = matchMinutes * 60 * 1000;

            const rueckPoints = rueck.points;
            const steigPoints = steig.points;

            const startIdx = requireThreshold
                ? rueckPoints.findIndex(point => point.temp > threshold)
                : 0;
            if (startIdx === -1) {
                return null;
            }

            let min = null;
            let max = null;
            let current = null;
            let steigIndex = 0;

            for (let i = startIdx; i < rueckPoints.length; i += 1) {
                const rueckPoint = rueckPoints[i];
                const rueckTime = rueckPoint.time.getTime();

                while (steigIndex < steigPoints.length && steigPoints[steigIndex].time.getTime() < rueckTime - matchMs) {
                    steigIndex += 1;
                }

                let best = null;
                const candidates = [];
                if (steigIndex < steigPoints.length) {
                    candidates.push(steigPoints[steigIndex]);
                }
                if (steigIndex > 0) {
                    candidates.push(steigPoints[steigIndex - 1]);
                }

                candidates.forEach(candidate => {
                    const diff = Math.abs(candidate.time.getTime() - rueckTime);
                    if (diff <= matchMs && (!best || diff < best.diff)) {
                        best = { temp: candidate.temp, diff };
                    }
                });

                if (!best) {
                    continue;
                }

                const delta = best.temp - rueckPoint.temp;
                if (min === null || delta < min) {
                    min = delta;
                }
                if (max === null || delta > max) {
                    max = delta;
                }
                current = delta;
            }

            if (current === null || min === null || max === null) {
                return null;
            }

            return { current, min, max };
        }

        function applySmoothing(points, mode, windowMinutes) {
            if (!points || points.length === 0) {
                return points;
            }

            switch (mode) {
                case 'A':
                    return smoothMedian(points, windowMinutes);
                case 'B':
                    return smoothEma(points, windowMinutes);
                default:
                    return points;
            }
        }

        function parseExternalTimestamp(value) {
            if (!value) {
                return new Date(NaN);
            }
            if (value instanceof Date) {
                return value;
            }
            if (typeof value !== 'string') {
                return new Date(value);
            }
            const normalized = value.includes(' ') ? value.replace(' ', 'T') : value;
            const hasTimezone = /[zZ]|[+-]\d{2}:\d{2}$/.test(normalized);
            return new Date(hasTimezone ? normalized : `${normalized}Z`);
        }

        function filterPointsByRange(points, timeRange) {
            if (!points || points.length === 0) {
                return points;
            }
            const from = timeRange.from.getTime();
            const to = timeRange.to.getTime();
            return points.filter(point => {
                const t = point.time.getTime();
                return t >= from && t <= to;
            });
        }

        function smoothMedian(points, windowMinutes) {
            const windowMs = windowMinutes * 60 * 1000;
            const result = [];
            let start = 0;
            let end = 0;

            for (let i = 0; i < points.length; i += 1) {
                const t = points[i].time.getTime();
                while (start < points.length && points[start].time.getTime() < t - windowMs) {
                    start += 1;
                }
                while (end < points.length && points[end].time.getTime() <= t + windowMs) {
                    end += 1;
                }

                const windowValues = [];
                for (let j = start; j < end; j += 1) {
                    windowValues.push(points[j].temp);
                }

                windowValues.sort((a, b) => a - b);
                const median = windowValues[Math.floor(windowValues.length / 2)];
                result.push({ time: points[i].time, temp: median });
            }

            return result;
        }

        function smoothEma(points, windowMinutes) {
            const windowMs = Math.max(1, windowMinutes * 60 * 1000);
            const result = [];
            let smoothed = points[0].temp;
            result.push({ time: points[0].time, temp: smoothed });

            for (let i = 1; i < points.length; i += 1) {
                const dt = Math.max(1, points[i].time.getTime() - points[i - 1].time.getTime());
                const alpha = 1 - Math.exp(-dt / windowMs);
                smoothed = smoothed + alpha * (points[i].temp - smoothed);
                result.push({ time: points[i].time, temp: smoothed });
            }

            return result;
        }


        window.addEventListener('resize', () => {
            if (currentView === 'graph') {
                renderGraphs(latestGraphSeries, latestExternalSeries, lastGraphError);
            } else if (currentView === 'flow') {
                renderFlowGraph(latestExternalSeries, lastExternalError);
            } else if (currentView === 'monitor') {
                renderMonitorGraph(latestExternalSeries, lastExternalError);
            }
        });

        const connection = new signalR.HubConnectionBuilder()
            .withUrl("/live")
            .withAutomaticReconnect()
            .build();

        connection.on("reading", (data) => {
            sensors.set(data.deviceMac, data);
            updateDisplay();
        });

        connection.start()
            .then(() => {
                setConnectionState('ok', 'Verbunden mit Backend');
                setShutdownEnabled(true);
            })
            .catch(err => {
                setConnectionState('error', 'Verbindung fehlgeschlagen');
                setShutdownEnabled(false);
                console.error('SignalR Error:', err);
            });

        connection.onreconnecting(() => {
            setConnectionState('warn', 'Verbindung wird wiederhergestellt...');
            setShutdownEnabled(false);
        });

        connection.onreconnected(() => {
            setConnectionState('ok', 'Verbunden mit Backend');
            setShutdownEnabled(true);
        });

        function updateDisplay() {
            const container = document.getElementById('sensorsContainer');
            const noData = document.getElementById('noData');

            if (sensors.size === 0) {
                container.style.display = 'none';
                noData.style.display = 'block';
                return;
            }

            container.style.display = 'grid';
            noData.style.display = 'none';

            container.innerHTML = '';

            sensors.forEach((data, mac) => {
                const card = document.createElement('div');
                card.className = 'sensor-card';

                const time = new Date(data.timestamp).toLocaleTimeString('de-DE');
                const temp = data.temperatureC?.toFixed(1) ?? 'n/a';
                const humidity = data.humidityPercent ?? 'n/a';
                const battery = data.batteryPercent ?? 'n/a';
                const rssi = data.rssi;
                const deviceName = getDeviceName(mac);

                card.innerHTML = `
                    <div class="sensor-header">
                        <div>
                            <div class="sensor-name">${deviceName}</div>
                            <div class="sensor-mac">(${mac})</div>
                        </div>
                        <div class="sensor-time">${time}</div>
                    </div>
                    <div class="sensor-data">
                        <div class="data-item">
                            <div class="data-label">
                                <span class="icon">🌡️</span>
                                Temperatur
                            </div>
                            <div class="data-value temp">${temp} °C</div>
                        </div>
                        <div class="data-item">
                            <div class="data-label">
                                <span class="icon">💧</span>
                                Luftfeuchtigkeit
                            </div>
                            <div class="data-value humidity">${humidity} %</div>
                        </div>
                        <div class="data-item">
                            <div class="data-label">
                                <span class="icon">🔋</span>
                                Batterie
                            </div>
                            <div class="data-value battery">${battery} %</div>
                        </div>
                        <div class="data-item">
                            <div class="data-label">
                                <span class="icon">📡</span>
                                Signal
                            </div>
                            <div class="data-value signal">${rssi} dBm</div>
                        </div>
                    </div>
                `;

                container.appendChild(card);
            });
        }
