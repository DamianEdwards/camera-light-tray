'use strict';

const paths = {
  power: '<path d="M12 3v8m-5.4-5.3a8 8 0 1 0 10.8 0"/>',
  settings: '<path d="m9 3-.7 2.2-2 .9-2.1-.5-1.5 2.6 1.5 1.7v2.3l-1.5 1.7 1.5 2.6 2.1-.5 2 .9L9 21h3l.7-2.1 2-.9 2.1.5 1.5-2.6-1.5-1.7V12l1.5-1.7-1.5-2.6-2.1.5-2-.9L12 3Z"/><circle cx="10.5" cy="12" r="3"/>',
  sun: '<circle cx="12" cy="12" r="4"/><path d="M12 1v2m0 18v2M1 12h2m18 0h2M4.2 4.2l1.4 1.4m12.8 12.8 1.4 1.4M4.2 19.8l1.4-1.4M18.4 5.6l1.4-1.4"/>',
  dim: '<circle cx="12" cy="12" r="3"/><path d="M12 3v2m0 14v2M3 12h2m14 0h2"/>',
  warm: '<path d="M8 4a3 3 0 0 1 6 0v10a5 5 0 1 1-6 0V4Z"/><path d="M11 9v9m8-8 2-2m-2 7h3"/>',
  cool: '<path d="M8 4a3 3 0 0 1 6 0v10a5 5 0 1 1-6 0V4Z"/><path d="M11 9v9m9-13v6m-3-3h6"/>',
  camera: '<rect x="3" y="6" width="14" height="12" rx="3"/><path d="m17 10 4-3v10l-4-3"/><circle cx="10" cy="12" r="3"/>',
  apps: '<rect x="3" y="3" width="7" height="7" rx="1.5"/><rect x="14" y="3" width="7" height="7" rx="1.5"/><rect x="3" y="14" width="7" height="7" rx="1.5"/><rect x="14" y="14" width="7" height="7" rx="1.5"/>',
  light: '<rect x="3" y="3" width="18" height="13" rx="3"/><path d="M12 16v5m-4 0h8"/>',
  check: '<path d="m5 12 4 4L19 6"/>',
  chevron: '<path d="m8 5 7 7-7 7"/>',
  info: '<circle cx="12" cy="12" r="9"/><path d="M12 11v6m0-10v.1"/>',
  chat: '<path d="M4 4h16v12H10l-6 4V4Z"/><path d="M8 8h8m-8 4h5"/>',
  tools: '<path d="M5 7h14M5 17h14"/><circle cx="9" cy="7" r="3"/><circle cx="15" cy="17" r="3"/>',
  home: '<path d="m3 10 9-7 9 7v11h-6v-7H9v7H3Z"/>',
  close: '<path d="m6 6 12 12M6 18 18 6"/>'
};
const icon = (name) => `<svg class="line-icon" viewBox="0 0 24 24" aria-hidden="true">${paths[name] || paths.light}</svg>`;
const art = (model, on, name) => `<svg class="product-art ${on === null ? 'power-unknown' : ''}" viewBox="0 0 160 180" role="img" aria-label="${name}, ${on === null ? 'power unknown' : on ? 'illuminated' : 'off'}"><use href="#${model}"></use>${on === null ? '<circle cx="124" cy="132" r="17" fill="#7b8790" stroke="#d8e0e6" stroke-width="2"/><text x="124" y="140" text-anchor="middle" fill="#fff" font-size="23" font-family="Segoe UI, sans-serif" font-weight="600" aria-hidden="true">?</text>' : ''}</svg>`;
const panelColor = temperature => {
  const blend = Math.max(0, Math.min(1, (temperature - 2900) / 4100));
  return `rgb(${[255, 230, 182].map((warm, i) => Math.round(warm + ([226, 242, 255][i] - warm) * blend)).join(' ')})`;
};
const escapeText = (value) => String(value).replace(/[&<>"']/g, char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[char]);

const prototype = {
  view: 'compare', layout: 'studio', scenario: 'active', page: 'apps',
  cameraActive: true, cameraUnknown: false, paused: false, noApps: false,
  expanded: 0, selectedLight: 0, allCameras: false, showAllApps: false,
  appSearch: '', startup: false, enableAutomation: true, flyoutVisible: true, appearance: 'light',
  lights: [], apps: [], cameras: []
};
let toastTimer;

function makeExample() {
  prototype.lights = [
    { id: 0, name: 'Key Light', model: 'key-light', on: true, auto: true, manual: false, brightness: 26, temperature: 4900, offline: false, tls: false },
    { id: 1, name: 'Key Light Air', model: 'key-light-air', on: true, auto: true, manual: false, brightness: 26, temperature: 4900, offline: false, tls: false }
  ];
  prototype.apps = [
    { name: 'Microsoft Teams', description: 'Meeting app · selected', icon: 'chat', selected: true, priority: true },
    { name: 'Windows Camera', description: 'Camera app · selected', icon: 'camera', selected: true, priority: true },
    { name: 'Camera Light Tray', description: 'Select to test with local preview', icon: 'light', selected: true, priority: true },
    { name: 'Signal', description: 'Communication app', icon: 'chat', selected: false, priority: true },
    { name: 'Zoom', description: 'Communication app', icon: 'camera', selected: false, priority: true },
    { name: 'Example Browser', description: 'Running application', icon: 'apps', selected: false, priority: false }
  ];
  prototype.cameras = [
    { name: 'Logitech BRIO', selected: true, description: 'Available' },
    { name: 'Cam Link 4K', selected: false, description: 'Available' },
    { name: 'Virtual Camera', selected: false, description: 'Available' }
  ];
}

function notice(message) {
  document.querySelector('#interaction-log').textContent = message;
  const toast = document.querySelector('#toast');
  toast.textContent = message;
  toast.classList.add('visible');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => toast.classList.remove('visible'), 3500);
}

function chosenApps() { return prototype.apps.filter(app => app.selected).length; }
function activeAutomation() { return prototype.enableAutomation && !prototype.paused && chosenApps() > 0; }
function eligibleCamera(state) { return state.allCameras || state.cameras[0].selected; }
function eligibleActivity(state) { return state.cameraActive && state.apps[0].selected && eligibleCamera(state); }
function automate() {
  for (const light of prototype.lights) {
    if (!light.auto || light.manual || light.offline || light.tls) continue;
    if (!activeAutomation()) light.on = false;
    else if (!prototype.cameraUnknown) light.on = eligibleActivity(prototype);
  }
}

function stripStatus(state) {
  if (!state.enableAutomation) return { title: 'Automation off', sub: 'Quick controls still available', quiet: true };
  if (state.noApps) return { title: 'Choose trigger apps', sub: 'No apps selected · lights will not turn on automatically', waiting: true, action: 'Choose apps' };
  if (state.paused) return { title: 'Automation paused', sub: 'Your previous light states are restored', quiet: true };
  if (state.cameraUnknown) return { title: 'Camera state unknown', sub: 'Holding owned lights · checking again', waiting: true, action: 'Details' };
  if (state.cameraActive && !eligibleActivity(state)) return { title: 'Camera use ignored', sub: state.apps[0].selected ? 'Logitech BRIO is not a trigger camera' : 'Microsoft Teams is not selected', quiet: true };
  return state.cameraActive ?
    { title: 'Camera in use', sub: 'Microsoft Teams · Logitech BRIO' } :
    { title: 'Ready for your next call', sub: 'Selected apps will trigger these lights', quiet: true };
}

function stateForComparison() {
  return { ...prototype, noApps: false, enableAutomation: true, paused: false, cameraUnknown: false, cameraActive: true,
    scenario: 'active', expanded: 0, selectedLight: 0, allCameras: false,
    apps: [{ name: 'Microsoft Teams', selected: true }], cameras: [{ name: 'Logitech BRIO', selected: true }],
    lights: [
      { id: 0, name: 'Key Light', model: 'key-light', on: true, auto: true, manual: false, brightness: 26, temperature: 4900, offline: false, tls: false },
      { id: 1, name: 'Key Light Air', model: 'key-light-air', on: true, auto: true, manual: false, brightness: 26, temperature: 4900, offline: false, tls: false }
    ] };
}

function power(light, passive) {
  const label = light.offline ? `${light.name} unreachable` : light.tls ? `${light.name} unsupported` : `Turn ${light.name} ${light.on ? 'off' : 'on'}`;
  return `<button class="power-button" aria-label="${label}" ${light.offline || light.tls ? '' : `aria-pressed="${light.on}"`} data-power="${light.id}" ${passive ? 'data-passive' : ''} ${light.offline || light.tls ? 'disabled' : ''}>${icon('power')}</button>`;
}

function autoControl(light, passive, compact, expanded) {
  return `<div class="light-automation">
    <button class="auto-toggle" aria-label="Automate ${light.name}" aria-pressed="${light.auto}" data-auto="${light.id}" ${passive ? 'data-passive' : ''} ${light.tls ? 'disabled' : ''}></button>
    <span>${light.manual ? 'Manual this session' : light.auto ? 'Auto with camera' : 'Manual only'}</span>
    ${light.manual ? `<button class="resume-auto" data-resume="${light.id}">Resume auto</button>` : ''}
    ${compact ? `<button class="expand-control" aria-label="${expanded ? 'Hide' : 'Show'} ${light.name} adjustments" aria-expanded="${expanded}" data-expand="${light.id}" ${passive ? 'data-passive' : ''}>Adjust ${icon('chevron')}</button>` : ''}
  </div>`;
}

function sliders(light, prefix, passive) {
  const disable = light.offline || light.tls || passive;
  return `<div class="sliders ${light.offline || light.tls ? 'unavailable' : ''}">
    <div><label class="slider-label" for="${prefix}-brightness-${light.id}"><span>Brightness</span><output data-reading="brightness" data-device="${light.id}">${light.tls ? 'Unavailable' : `${light.brightness}%${light.offline ? ' · last known' : ''}`}</output></label>
    <div class="range-row">${icon('dim')}<input class="brightness" id="${prefix}-brightness-${light.id}" type="range" min="0" max="100" step="1" value="${light.brightness}" aria-label="${light.name} brightness" aria-valuetext="${light.brightness} percent" data-slider="brightness" data-device="${light.id}" ${disable ? 'disabled' : ''}>${icon('sun')}</div></div>
    <div><label class="slider-label" for="${prefix}-temperature-${light.id}"><span>Temperature</span><output data-reading="temperature" data-device="${light.id}">${light.tls ? 'Unavailable' : `${light.temperature.toLocaleString('en-US')} K${light.offline ? ' · last known' : ''}`}</output></label>
    <div class="range-row">${icon('warm')}<input class="temperature" id="${prefix}-temperature-${light.id}" type="range" min="2900" max="7000" step="100" value="${light.temperature}" aria-label="${light.name} color temperature" aria-valuetext="${light.temperature} kelvin" data-slider="temperature" data-device="${light.id}" ${disable ? 'disabled' : ''}>${icon('cool')}</div></div>
  </div>`;
}

function stateLabel(light) {
  if (light.tls) return 'Unsupported TLS model';
  if (light.offline) return 'Unreachable · power unknown';
  return `<span class="${light.on ? 'on-text' : ''}">${light.on ? 'On' : 'Off'}</span><span aria-hidden="true">·</span><span>${light.manual ? 'Manual' : light.auto ? 'Automatic' : 'Not automated'}</span>`;
}

function lightRow(light, layout, state, prefix, passive) {
  const compact = layout === 'compact';
  const expanded = state.expanded === light.id;
  return `<section class="light-row ${light.on && !light.offline && !light.tls ? 'is-on' : ''}" data-light="${light.id}" aria-label="${light.name}" style="--lit-fill:${panelColor(light.temperature)}">
    <div class="light-heading">
      ${art(light.model, light.offline || light.tls ? null : light.on, light.name)}
      <div><h4 class="light-name">${light.name}</h4><div class="light-state">${stateLabel(light)}</div></div>
      ${power(light, passive)}
    </div>
    ${autoControl(light, passive, compact, expanded)}
    ${!compact || expanded ? sliders(light, prefix, passive) : ''}
    ${light.offline ? '<p class="device-error">Reconnecting automatically. Other lights still work.</p>' : ''}
    ${light.tls ? '<p class="device-error">This light uses TLS. Control is not supported yet.</p>' : ''}
  </section>`;
}

function galleryLight(light, state, passive) {
  return `<section class="gallery-device ${light.on && !light.offline && !light.tls ? 'is-on' : ''} ${state.selectedLight === light.id ? 'selected' : ''}" data-light="${light.id}" style="--lit-fill:${panelColor(light.temperature)}">
    ${power(light, passive)}
    ${art(light.model, light.offline || light.tls ? null : light.on, light.name)}
    <button class="gallery-select" data-select-light="${light.id}" ${passive ? 'data-passive' : ''}>${light.name}</button>
    <div class="light-state">${stateLabel(light)}</div>
    ${autoControl(light, passive, false, false)}
  </section>`;
}

function flyout(layout, state, prefix, passive = false) {
  const status = stripStatus(state);
  const selected = state.lights.find(light => light.id === state.selectedLight) || state.lights[0];
  let contents;
  if (state.scenario === 'empty') {
    contents = `<div class="empty-lights">${art('key-light-air', false, 'Example light')}<h3>Bring your lights in</h3><p>Choose your LAN and discover lights. Nothing turns on until you enable it.</p><button data-settings="lights">Add lights</button></div>`;
  } else if (state.scenario === 'discovering') {
    contents = '<div class="empty-lights"><div class="spinner" aria-hidden="true"></div><h3>Looking for lights</h3><p>Searching your selected LAN. Existing controls will be ready when discovery finishes.</p><button data-finish-discovery>Finish example discovery</button></div>';
  } else if (layout === 'gallery') {
    contents = `<div class="gallery-grid">${state.lights.map(light => galleryLight(light, state, passive)).join('')}</div>
    <div class="gallery-controls"><h3>Adjust ${selected.name}</h3>${sliders(selected, prefix, passive)}${selected.manual ? `<button class="resume-auto" data-resume="${selected.id}">Resume auto</button>` : ''}${selected.offline ? '<p class="device-error">This light is unreachable. Reconnecting automatically.</p>' : ''}${selected.tls ? '<p class="device-error">TLS control is not supported yet.</p>' : ''}</div>`;
  } else {
    contents = state.lights.map(light => lightRow(light, layout, state, prefix, passive)).join('');
  }
  return `<div class="flyout ${layout}" role="region" aria-label="${layout} tray flyout">
    <header><div class="flyout-brand"><img src="assets/app-icon.svg" alt=""><h3>Camera Light Tray</h3></div><button class="icon-button" aria-label="Open Camera Light Tray settings" data-settings="apps">${icon('settings')}</button></header>
    <div class="automation-strip">
      <div><div class="status-line"><span class="status-dot ${status.waiting ? 'waiting' : status.quiet ? 'quiet' : ''}"></span>${status.title}</div><p class="strip-sub">${status.sub}</p></div>
      ${status.action ? `<button class="pause-button" data-settings="${state.noApps ? 'apps' : 'automation'}">${status.action}</button>` : `<button class="pause-button" data-pause ${passive ? 'data-passive' : ''}>${state.paused ? 'Resume' : 'Pause'}</button>`}
    </div>
    ${contents}
    <footer><span>${state.scenario === 'empty' ? 'No lights configured' : state.scenario === 'discovering' ? 'Discovery in progress' : `${state.lights.filter(light => light.auto && !light.tls).length} lights set to auto`}</span><button class="footer-link" data-settings="apps">Manage triggers ${icon('chevron')}</button></footer>
  </div>`;
}

function renderComparisons() {
  document.querySelectorAll('[data-comparison]').forEach(host => {
    host.innerHTML = flyout(host.dataset.comparison, stateForComparison(), `compare-${host.dataset.comparison}`, true);
  });
}

function renderFlyout() {
  prototype.noApps = chosenApps() === 0;
  const host = document.querySelector('#focus-host');
  host.innerHTML = flyout(prototype.layout, prototype, 'focus');
  host.hidden = !prototype.flyoutVisible;
}

function applyScenario(scenario) {
  prototype.scenario = scenario;
  makeExample();
  prototype.cameraActive = ['active', 'offline', 'unsupported'].includes(scenario);
  prototype.cameraUnknown = scenario === 'unknown';
  prototype.paused = scenario === 'paused';
  prototype.enableAutomation = true;
  prototype.noApps = scenario === 'none';
  if (scenario === 'none') prototype.apps.forEach(app => app.selected = false);
  if (scenario === 'offline') prototype.lights[1].offline = true;
  if (scenario === 'unsupported') { prototype.lights[1].tls = true; prototype.lights[1].auto = false; prototype.lights[1].on = false; }
  if (scenario === 'unknown') prototype.lights.forEach(light => light.on = true);
  else automate();
  renderFlyout();
  renderSettings();
  notice(scenario === 'none' ? 'No apps selected: automatic activation is disabled.' : `Example state: ${document.querySelector(`#scenario option[value="${scenario}"]`).textContent}.`);
}

function changeView(view) {
  prototype.view = view;
  document.querySelectorAll('.review-view').forEach(section => section.hidden = section.id !== view);
  document.querySelectorAll('[data-view]').forEach(button => button.setAttribute('aria-pressed', String(button.dataset.view === view)));
  history.replaceState(null, '', `#${view}`);
  document.querySelector('#toast').classList.remove('visible');
  if (view === 'focus') renderFlyout();
  if (view === 'settings') renderSettings();
}

const pages = {
  automation: { title: 'Automation', icon: 'home' }, apps: { title: 'Apps', icon: 'apps' },
  cameras: { title: 'Cameras', icon: 'camera' }, lights: { title: 'Lights', icon: 'light' },
  general: { title: 'General', icon: 'settings' }
};

function appsPage() {
  const apps = prototype.apps.map((app, index) => ({ ...app, index })).filter(app => (prototype.showAllApps || app.priority) && app.name.toLowerCase().includes(prototype.appSearch.toLowerCase()));
  const count = chosenApps();
  return `<h2>Apps that trigger your lights</h2><p class="page-description">Only the apps you select can turn on your lights. They stay on while any selected app uses a monitored camera.</p>
    <div class="context-banner">${icon(count > 0 ? 'check' : 'info')}<div><strong>${count > 0 ? `${count} apps selected` : 'Select an app to start'}</strong><p>${count > 0 ? 'Other apps can use the camera without triggering lighting.' : 'No selection means no automatic activation—not every app.'}</p></div></div>
    <div class="search-row"><input type="search" id="app-search" placeholder="Search apps" aria-label="Search example apps" value="${escapeText(prototype.appSearch)}"><span>${count} selected</span></div>
    <div class="app-list" id="app-results">${apps.map(app => `<label class="app-entry"><span class="app-avatar">${icon(app.icon)}</span><span><strong>${app.name}</strong><small>${prototype.cameraActive && app.index === 0 ? `Using Logitech BRIO · ${eligibleActivity(prototype) && activeAutomation() ? 'triggering lights' : 'not triggering lights'}` : app.description.replace(' · selected', '')}</small></span><input type="checkbox" data-app="${app.index}" ${app.selected ? 'checked' : ''} aria-label="Trigger lights for ${app.name}"></label>`).join('') || '<div class="settings-empty"><h3>No matching apps</h3><p>Try a different search, or add an executable below.</p></div>'}</div>
    <div class="section-label"><button class="text-button" data-show-all>${prototype.showAllApps ? 'Show suggested apps' : 'Show all running apps'}</button><button class="text-button" data-clear-apps>Clear selection</button></div>
    <details class="detail-disclosure"><summary>Add an app by executable path</summary><p>Use this for apps that are not currently running. This review does not access your files.</p><div class="search-row"><input id="executable-path" placeholder="C:\\Path\\App.exe" aria-label="Example executable path"><button class="plain-button" data-add-executable>Add</button></div></details>
    <details class="detail-disclosure"><summary>How suggestions work</summary><p>Selected apps and active camera clients appear first, followed by installed apps that declare webcam capability and known communication apps. Suggestions are not permission grants. Nothing is selected automatically.</p><p>Different executable paths can represent different app identities. The native build will expose those details on demand rather than under every row.</p></details>`;
}

function cameraPage() {
  return `<h2>Trigger cameras</h2><p class="page-description">Lighting follows selected apps using these cameras. Changing the preview camera does not change this choice.</p>
    <div class="choice-list">
      <div class="choice-entry">${icon('camera')}<label><span><strong>All cameras</strong><small>Any camera used by a selected app</small></span><input type="checkbox" data-all-cameras ${prototype.allCameras ? 'checked' : ''}></label></div>
      ${prototype.cameras.map((camera, index) => `<div class="choice-entry">${icon('camera')}<label><span>${camera.name}<small>${camera.description}${camera.selected && !prototype.allCameras ? ' · selected' : ''}</small></span><input type="checkbox" data-camera="${index}" ${prototype.allCameras || camera.selected ? 'checked' : ''} ${prototype.allCameras ? 'disabled' : ''} aria-label="Trigger on ${camera.name}"></label></div>`).join('')}
    </div>
    <div class="context-banner">${icon('info')}<div><strong>Preview is a separate action</strong><p>A camera preview only triggers lights when Camera Light Tray is also selected under Apps.</p><button class="text-button" data-preview-note>Open local preview</button></div></div>
    <details class="detail-disclosure"><summary>Camera status and troubleshooting</summary><p>Unknown means the monitor does not have sufficient fresh information. It keeps owned lights in their existing state rather than guessing the camera is off. Unavailable cameras need a new selection, not helper installation.</p></details>`;
}

function lightsPage() {
  return `<h2>Your lights</h2><p class="page-description">Choose which lights follow your camera. New lights are never enabled automatically.</p>
    <div class="setting-row"><div><strong>Connection</strong><p>Discovery and control use this LAN interface.</p></div><select aria-label="Example LAN interface"><option>Ethernet</option><option>Wi-Fi</option></select></div>
    ${prototype.scenario === 'empty' ? '<div class="settings-empty"><h3>No lights yet</h3><p>Discover lights on your selected LAN, then enable the ones you want.</p></div>' : prototype.lights.map(light => `<div class="device-setting">${art(light.model, light.offline || light.tls ? null : light.on, light.name)}<div><strong>${light.name}</strong><small>${light.tls ? 'TLS · not supported yet' : light.offline ? 'Unreachable · reconnecting' : 'Connected · classic HTTP model'}</small></div><label><span class="connection-label">${light.auto ? 'Auto' : 'Manual'}</span><input type="checkbox" data-auto-setting="${light.id}" ${light.auto ? 'checked' : ''} ${light.tls ? 'disabled' : ''} aria-label="Automate ${light.name}"></label></div>`).join('')}
    <div class="section-label"><button class="text-button" data-discover>Discover lights</button><button class="text-button" data-connection-test>Test connections</button></div>
    <details class="detail-disclosure"><summary>Advanced connection details</summary><p>Manual addresses, interface bindings and response details belong here. No addresses in this mockup come from your machine. Discovery will not change VPN routes.</p><p>Classic Key Light and Key Light Air HTTP control is supported today. TLS models must remain clearly marked unsupported.</p></details>`;
}

function automationPage() {
  const apps = chosenApps();
  return `<h2>Lighting, on cue</h2><p class="page-description">Set up your triggers once. Then get back to your meeting.</p>
    <div class="setup-step">${icon(apps ? 'check' : 'apps')}<div><strong>${apps ? `${apps} trigger apps selected` : 'Choose trigger apps'}</strong><small>${apps ? 'Only selected apps can activate lights' : 'No selected apps means no automatic activation'}</small></div><button class="text-button" data-settings-page="apps">Edit</button></div>
    <div class="setup-step">${icon('camera')}<div><strong>${prototype.allCameras ? 'All cameras' : prototype.cameras.filter(camera => camera.selected).map(camera => camera.name).join(', ')}</strong><small>Camera filter is independent of app selection</small></div><button class="text-button" data-settings-page="cameras">Edit</button></div>
    <div class="setup-step">${icon(prototype.scenario === 'empty' ? 'light' : 'check')}<div><strong>${prototype.scenario === 'empty' ? 'Add your lights' : `${prototype.lights.filter(light => light.auto).length} lights set to auto`}</strong><small>Previous light states are preserved</small></div><button class="text-button" data-settings-page="lights">Edit</button></div>
    <div class="setting-row"><div><strong>Enable automation</strong><p>${apps ? 'Selected lights will follow your camera activity.' : 'Choose apps before automation can turn lights on.'}</p></div><input type="checkbox" aria-label="Enable simulated automation" data-enable ${prototype.enableAutomation ? 'checked' : ''}></div>
    <div class="setting-row"><div><strong>Ignore brief camera checks</strong><p>Wait before turning lights on.</p></div><select aria-label="Activation delay"><option>0.5 seconds</option><option>1 second</option><option>2 seconds</option></select></div>
    <div class="setting-row"><div><strong>Keep lights on after capture stops</strong><p>A little breathing room between calls.</p></div><select aria-label="Leave grace"><option>2.5 seconds</option><option>5 seconds</option><option>10 seconds</option></select></div>
    <details class="detail-disclosure" ${prototype.cameraUnknown ? 'open' : ''}><summary>Monitor details</summary><p>${prototype.cameraUnknown ? 'Camera state unknown. Keeping owned lights unchanged and checking for fresh reports.' : 'Example monitor connected. Automatic retries recover transient failures.'}</p><p>Normal monitoring does not need elevation or the optional legacy helper.</p></details>`;
}

function generalPage() {
  return `<h2>General</h2><p class="page-description">A small utility, out of your way.</p>
    <div class="setting-row"><div><strong>Start at sign-in</strong><p>Start quietly in the tray. No administrator prompt.</p></div><input type="checkbox" data-startup aria-label="Simulate start at sign-in preference" ${prototype.startup ? 'checked' : ''}></div>
    <div class="setting-row"><div><strong>Appearance</strong><p>Light and dark examples for this review.</p></div><select id="settings-appearance" aria-label="Settings appearance"><option value="light" ${prototype.appearance === 'light' ? 'selected' : ''}>Light</option><option value="dark" ${prototype.appearance === 'dark' ? 'selected' : ''}>Dark</option></select></div>
    <div class="setting-row"><div><strong>When the window closes</strong><p>Stay in the tray. Camera preview stops.</p></div><span class="connection-label">Keep running</span></div>
    <details class="detail-disclosure"><summary>Experimental tools</summary><p>The optional legacy ETW helper is a diagnostic tool, not a setup requirement. Installation needs elevation; do not use it to fix normal sensor or LAN issues.</p><button class="text-button" data-helper-note>Legacy helper details</button></details>
    <details class="detail-disclosure"><summary>Privacy</summary><p>Background monitoring reads camera activity metadata. Explicit preview stays local and never records or uploads frames. The application sends no telemetry.</p></details>`;
}

function renderSettings() {
  const page = { apps: appsPage, cameras: cameraPage, lights: lightsPage, automation: automationPage, general: generalPage }[prototype.page];
  document.querySelector('#settings-host').innerHTML = `<div class="settings-window ${prototype.appearance === 'dark' ? 'dark' : ''}" role="region" aria-label="Example Camera Light Tray settings window">
    <div class="window-titlebar"><span><img src="assets/app-icon.svg" alt="">Camera Light Tray</span><div class="window-controls" aria-hidden="true"><span>−</span><span>□</span></div><button class="text-button" data-close-settings aria-label="Close example settings and return to tray">Close</button></div>
    <div class="settings-shell"><nav class="settings-nav" aria-label="Settings sections">${Object.entries(pages).map(([key, value]) => `<button data-settings-page="${key}" ${prototype.page === key ? 'aria-current="page"' : ''}>${icon(value.icon)}${value.title}</button>`).join('')}<div class="nav-status">Prototype only<br>No device access</div></nav><div class="settings-body">${page()}</div></div></div>`;
}

function openSettings(page) {
  prototype.page = page;
  changeView('settings');
  document.querySelector('#settings-host h2')?.scrollIntoView({ block: 'nearest' });
}

document.addEventListener('click', event => {
  const target = event.target.closest('button');
  if (!target) return;
  if (target.dataset.passive !== undefined) { changeView('focus'); notice('Comparison controls are previews. Try them here.'); return; }
  if (target.dataset.view) { changeView(target.dataset.view); return; }
  if (target.dataset.choose) {
    prototype.layout = target.dataset.choose;
    document.querySelector('#layout').value = prototype.layout;
    prototype.flyoutVisible = true;
    changeView('focus'); return;
  }
  if (target.dataset.settings) { openSettings(target.dataset.settings); return; }
  if (target.dataset.settingsPage) { prototype.page = target.dataset.settingsPage; renderSettings(); return; }
  if (target.dataset.closeSettings !== undefined) { changeView('focus'); return; }
  if (target.dataset.power !== undefined) {
    const light = prototype.lights[Number(target.dataset.power)];
    light.on = !light.on; light.manual = true;
    notice(`${light.name} ${light.on ? 'on' : 'off'} · manual for this camera session.`);
    renderFlyout(); return;
  }
  if (target.dataset.auto !== undefined || target.dataset.resume !== undefined) {
    const id = Number(target.dataset.auto ?? target.dataset.resume);
    const light = prototype.lights[id];
    light.auto = target.dataset.resume !== undefined ? true : !light.auto;
    light.manual = false; automate();
    notice(`${light.name}: ${light.auto ? 'automatic camera control resumed' : 'manual control only'}.`);
    renderFlyout(); renderSettings(); return;
  }
  if (target.dataset.expand !== undefined) { prototype.expanded = prototype.expanded === Number(target.dataset.expand) ? -1 : Number(target.dataset.expand); renderFlyout(); return; }
  if (target.dataset.selectLight !== undefined) { prototype.selectedLight = Number(target.dataset.selectLight); renderFlyout(); return; }
  if (target.dataset.pause !== undefined) { prototype.paused = !prototype.paused; automate(); renderFlyout(); notice(prototype.paused ? 'Automation paused. Only automatic changes are restored.' : 'Automation resumed.'); return; }
  if (target.dataset.clearApps !== undefined) { prototype.apps.forEach(app => app.selected = false); automate(); renderSettings(); renderFlyout(); notice('No apps selected: lights will not turn on automatically.'); return; }
  if (target.dataset.showAll !== undefined) { prototype.showAllApps = !prototype.showAllApps; renderSettings(); return; }
  if (target.dataset.addExecutable !== undefined) { notice('Mockup only: executable selection will use a native file picker. No files were read.'); return; }
  if (target.dataset.previewNote !== undefined) { notice('Preview belongs in a separate, explicit panel. This mockup never opens a camera.'); return; }
  if (target.dataset.helperNote !== undefined) { notice('Legacy diagnostics only. No installation or elevation is performed in this mockup.'); return; }
  if (target.dataset.connectionTest !== undefined) { notice('Simulated connection test only. The mockup makes no device requests.'); return; }
  if (target.dataset.discover !== undefined) { applyScenario('discovering'); changeView('focus'); document.querySelector('#scenario').value = 'discovering'; return; }
  if (target.dataset.finishDiscovery !== undefined) { applyScenario('idle'); document.querySelector('#scenario').value = 'idle'; prototype.lights.forEach(light => light.auto = false); renderFlyout(); notice('Example discovery found two lights. Both are disabled until selected.'); return; }
  if (target.id === 'reset') { applyScenario(document.querySelector('#scenario').value); return; }
  if (target.classList.contains('tray-button')) { prototype.flyoutVisible = !prototype.flyoutVisible; renderFlyout(); }
});

document.addEventListener('input', event => {
  const input = event.target;
  if (input.dataset.slider) {
    const light = prototype.lights[Number(input.dataset.device)];
    light[input.dataset.slider] = Number(input.value);
    input.setAttribute('aria-valuetext', `${input.value} ${input.dataset.slider === 'brightness' ? 'percent' : 'kelvin'}`);
    const flyoutElement = input.closest('.flyout');
    flyoutElement.querySelectorAll(`[data-reading="${input.dataset.slider}"][data-device="${light.id}"]`).forEach(output => output.textContent = input.dataset.slider === 'brightness' ? `${input.value}%` : `${Number(input.value).toLocaleString('en-US')} K`);
    if (input.dataset.slider === 'temperature') {
      flyoutElement.querySelectorAll(`[data-light="${light.id}"]`).forEach(device => device.style.setProperty('--lit-fill', panelColor(light.temperature)));
    }
  }
  if (input.id === 'app-search') {
    const start = input.selectionStart, end = input.selectionEnd;
    prototype.appSearch = input.value;
    renderSettings();
    const replacement = document.querySelector('#app-search');
    replacement.focus();
    if (start !== null && end !== null && replacement.type !== 'search') replacement.setSelectionRange(start, end);
  }
});

document.addEventListener('change', event => {
  const input = event.target;
  if (input.id === 'layout') { prototype.layout = input.value; renderFlyout(); }
  if (input.id === 'scenario') applyScenario(input.value);
  if (input.dataset.slider) { const light = prototype.lights[Number(input.dataset.device)]; light.manual = true; notice(`${light.name} adjusted · manual for this session. Resume auto when ready.`); renderFlyout(); }
  if (input.dataset.app !== undefined) { prototype.apps[Number(input.dataset.app)].selected = input.checked; automate(); renderSettings(); renderFlyout(); }
  if (input.dataset.allCameras !== undefined) { prototype.allCameras = input.checked; automate(); renderSettings(); renderFlyout(); }
  if (input.dataset.camera !== undefined) {
    prototype.cameras[Number(input.dataset.camera)].selected = input.checked;
    if (!prototype.cameras.some(camera => camera.selected)) { prototype.allCameras = true; notice('No specific cameras selected. The All cameras choice is now on.'); }
    automate(); renderSettings(); renderFlyout();
  }
  if (input.dataset.autoSetting !== undefined) { const light = prototype.lights[Number(input.dataset.autoSetting)]; light.auto = input.checked; light.manual = false; automate(); renderSettings(); renderFlyout(); }
  if (input.dataset.enable !== undefined) { prototype.enableAutomation = input.checked; automate(); renderFlyout(); renderSettings(); }
  if (input.dataset.startup !== undefined) { prototype.startup = input.checked; notice('Prototype preference only; Windows startup registration was not changed.'); }
  if (input.id === 'settings-appearance') { prototype.appearance = input.value; document.querySelector('.settings-window').classList.toggle('dark', input.value === 'dark'); }
});

makeExample();
renderComparisons();
renderFlyout();
renderSettings();
const requestedView = location.hash.slice(1);
changeView(['compare', 'focus', 'settings', 'details'].includes(requestedView) ? requestedView : 'compare');
