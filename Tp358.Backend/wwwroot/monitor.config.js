const deviceColors = ['#ef4444', '#3b82f6', '#10b981', '#f97316', '#6366f1', '#14b8a6', '#f43f5e', '#0ea5e9'];
const externalGraph = {
    name: 'ESP32',
    minTemp: 25,
    maxTemp: 48,
    delta: {
        startThreshold: 38,
        matchMinutes: 2
    },
    series: [
        { deviceId: 'Vorlauf', label: 'Vorlauf', color: '#f97316' },
        { deviceId: 'Rücklauf', label: 'Rücklauf', color: '#3b82f6' },
        { deviceId: 'EtagenRL', label: 'EtagenRL', color: '#facc15' }
    ]
};
const flowGraph = {
    name: 'Vor-/Rücklauf',
    minTemp: 25,
    maxTemp: 50,
    rowHeight: 400,
    heatEnergy: {
        flowRateLitersPerMinute: 2.6,
        densityKgPerLiter: 1,
        specificHeatKJPerKgK: 4.186
    }
};
const oldFlowGraph = {
    name: 'Steigleitung/Rücklauf',
    minTemp: 25,
    maxTemp: 50,
    rowHeight: 400,
    heatEnergy: {
        flowRateLitersPerMinute: 2.6,
        densityKgPerLiter: 1,
        specificHeatKJPerKgK: 4.186
    },
    delta: {
        startThreshold: 38,
        matchMinutes: 2,
        hotLabel: 'Steigleitung',
        coldLabel: 'Rücklauf'
    },
    series: [
        { deviceId: 'Steigleitung', label: 'Steigleitung', color: '#f97316' },
        { deviceId: 'Rücklauf', label: 'Rücklauf', color: '#3b82f6' }
    ]
};
const monitorGraph = {
    name: 'Monitor',
    minTemp: 25,
    maxTemp: 50,
    rowHeight: 400,
    windowMinutes: 20,
    refreshIntervalMs: 60000
};
const graphConfig = {
    hours: 24,
    minTemp: 15,
    maxTemp: 25,
    rowHeight: 150,
    padding: { left: 46, right: 16, top: 14, bottom: 28 }
};
const graphRefreshIntervalMs = 60000;
