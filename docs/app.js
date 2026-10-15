// AerControl site: live Spray demo, release info, platform-aware downloads, install tabs.
// Everything here is progressive: without JS the page shows every download and every guide.

const REPO = "parthh37/aercontrol";
const reducedMotion = matchMedia("(prefers-reduced-motion: reduce)").matches;

document.documentElement.classList.add("js");

// ---------------------------------------------------------------------------
// Platform detection
// ---------------------------------------------------------------------------

/**
 * Work out which AerControl build fits this visitor.
 *
 * Returns one of:
 *   "mac"        macOS (any Mac)
 *   "win-x64"    Windows on Intel/AMD
 *   "win-arm64"  Windows on Arm
 *   null         unknown, phone, Linux… (the page then shows all downloads equally)
 *
 * May be async: navigator.userAgentData.getHighEntropyValues() returns a Promise.
 */
async function detectPlatform() {
  // TODO(you): decide how to detect the visitor's platform. See the notes in the chat.
  return null;
}

// ---------------------------------------------------------------------------
// The Spray button: a playback of what AerControl puts on the wire
// ---------------------------------------------------------------------------

const sprayBtn = document.querySelector(".spray");
const wire = document.querySelector(".wire");
const canvas = document.querySelector(".mist");

// The dotted spray glyph from the app's button: one centre dot, then rings of smaller dots.
(function drawGlyph() {
  const rings = [
    { r: 0, n: 1, size: 7 },
    { r: 17, n: 6, size: 5 },
    { r: 31, n: 12, size: 3.6, offset: 15 },
    { r: 44, n: 12, size: 2.4 },
  ];
  let dots = "";
  for (const ring of rings) {
    for (let i = 0; i < ring.n; i++) {
      const a = ((i * 360) / ring.n + (ring.offset || 0)) * (Math.PI / 180);
      const x = (50 + ring.r * Math.cos(a)).toFixed(1);
      const y = (50 + ring.r * Math.sin(a)).toFixed(1);
      dots += `<circle cx="${x}" cy="${y}" r="${ring.size}"/>`;
    }
  }
  document.querySelector(".spray-glyph").innerHTML =
    `<svg viewBox="0 0 100 100" fill="currentColor">${dots}</svg>`;
})();

// Spray is {"mT":104,"mN":<n>,"rI":0} as an indefinite-length CBOR map.
// mN is a rolling message number; values 24–255 encode as 0x18 followed by one byte.
let messageNumber = 166;

function sprayBytes(mN) {
  const n = mN.toString(16).padStart(2, "0");
  return `bf 62 6d 54 18 68 62 6d 4e 18 ${n} 62 72 49 00 ff`;
}

// One log entry per spray: the write, then the device's reply underneath it.
function logSpray(mN) {
  wire.querySelector(".wire-idle")?.remove();
  const li = document.createElement("li");
  li.innerHTML = `<div><span class="dir">→ 6E400003</span> <span class="bytes">${sprayBytes(mN)}</span></div>`;
  wire.prepend(li);
  while (wire.children.length > 3) wire.lastElementChild.remove();

  // The device answers on 6E400001 with mT + 50 and the same mN.
  setTimeout(() => {
    li.insertAdjacentHTML("beforeend",
      `<div><span class="dir">← 6E400001</span> mT 154 · mN ${mN} · <span class="ack">ack 200, sprayed</span></div>`);
  }, reducedMotion ? 0 : 420);
}

sprayBtn.addEventListener("click", () => {
  logSpray(messageNumber);
  messageNumber = messageNumber >= 255 ? 100 : messageNumber + 1;

  sprayBtn.classList.add("is-pressed");
  setTimeout(() => sprayBtn.classList.remove("is-pressed"), 140);
  if (!reducedMotion) puff();
});

// Mist particles on a canvas around the button.
const ctx = canvas.getContext("2d");
let particles = [];
let animating = false;

function sizeCanvas() {
  const dpr = window.devicePixelRatio || 1;
  const { width, height } = canvas.getBoundingClientRect();
  canvas.width = width * dpr;
  canvas.height = height * dpr;
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
}

function puff() {
  sizeCanvas();
  const c = canvas.getBoundingClientRect();
  const b = sprayBtn.getBoundingClientRect();
  const ox = b.left + b.width / 2 - c.left;
  const oy = b.top + b.height * 0.12 - c.top;

  for (let i = 0; i < 110; i++) {
    // Mostly upward, fanning out like the aer's nozzle.
    const angle = -Math.PI / 2 + (Math.random() - 0.5) * 2.2;
    const speed = 2 + Math.random() * 7;
    particles.push({
      x: ox + (Math.random() - 0.5) * 40,
      y: oy,
      vx: Math.cos(angle) * speed,
      vy: Math.sin(angle) * speed,
      r: 0.8 + Math.random() * 2.8,
      life: 0,
      max: 60 + Math.random() * 70,
      hue: Math.random() < 0.6 ? "228, 243, 240" : "150, 200, 255",
    });
  }
  if (!animating) {
    animating = true;
    requestAnimationFrame(tick);
  }
}

function tick() {
  const { width, height } = canvas.getBoundingClientRect();
  ctx.clearRect(0, 0, width, height);
  particles = particles.filter((p) => p.life < p.max);
  for (const p of particles) {
    p.life++;
    p.vx *= 0.965;
    p.vy = p.vy * 0.965 - 0.015; // mist keeps drifting up after the burst
    p.x += p.vx;
    p.y += p.vy;
    const alpha = Math.max(0, 1 - p.life / p.max) * 0.85;
    ctx.beginPath();
    ctx.arc(p.x, p.y, p.r * (1 + p.life / p.max), 0, Math.PI * 2);
    ctx.fillStyle = `rgba(${p.hue}, ${alpha})`;
    ctx.fill();
  }
  if (particles.length) requestAnimationFrame(tick);
  else animating = false;
}

// ---------------------------------------------------------------------------
// Latest release: version label and download sizes
// ---------------------------------------------------------------------------

function formatSize(bytes) {
  const mb = bytes / 1e6;
  return mb < 10 ? `${mb.toFixed(1)} MB` : `${Math.round(mb)} MB`;
}

async function loadRelease() {
  try {
    const res = await fetch(`https://api.github.com/repos/${REPO}/releases/latest`, {
      headers: { Accept: "application/vnd.github+json" },
    });
    if (!res.ok) return; // rate-limited or offline: the static labels still work
    const release = await res.json();

    const date = new Date(release.published_at).toLocaleDateString(undefined, {
      year: "numeric", month: "short", day: "numeric",
    });
    for (const el of document.querySelectorAll("[data-version]")) {
      el.textContent = `Version ${release.tag_name.replace(/^v/, "")}, released ${date}`;
    }

    const sizes = new Map(release.assets.map((a) => [a.name, a.size]));
    for (const el of document.querySelectorAll("[data-asset]")) {
      const size = sizes.get(el.dataset.asset);
      if (size) el.textContent += ` · ${formatSize(size)}`;
    }
  } catch {
    // Network failure: leave the static text in place.
  }
}

// ---------------------------------------------------------------------------
// Install tabs (WAI-ARIA tabs pattern, arrow keys move between tabs)
// ---------------------------------------------------------------------------

const tabs = [...document.querySelectorAll("[role=tab]")];

function selectTab(name, focus = false) {
  for (const tab of tabs) {
    const on = tab.dataset.tab === name;
    tab.setAttribute("aria-selected", on);
    tab.tabIndex = on ? 0 : -1;
    document.querySelector(`[data-panel="${tab.dataset.tab}"]`).hidden = !on;
    if (on && focus) tab.focus();
  }
}

for (const tab of tabs) {
  tab.addEventListener("click", () => selectTab(tab.dataset.tab));
  tab.addEventListener("keydown", (e) => {
    const i = tabs.indexOf(tab);
    const step = { ArrowRight: 1, ArrowLeft: -1 }[e.key];
    if (step) selectTab(tabs[(i + step + tabs.length) % tabs.length].dataset.tab, true);
  });
}

// ---------------------------------------------------------------------------
// Boot
// ---------------------------------------------------------------------------

async function applyPlatform() {
  let platform = null;
  try {
    platform = await detectPlatform();
  } catch {
    platform = null;
  }

  const cta = document.querySelector("[data-cta]");
  const match = platform && cta.querySelector(`[data-platform="${platform}"]`);
  if (match) {
    cta.classList.add("is-detected");
    match.classList.add("is-match");
  }
  selectTab(platform?.startsWith("win") ? "win" : "mac");
}

selectTab("mac");
applyPlatform();
loadRelease();
