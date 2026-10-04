import { mkdir, readFile, writeFile, copyFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const root = fileURLToPath(new URL('.', import.meta.url));
const output = path.join(root, 'dist');
const site = 'https://openskiti.me';
const repo = 'https://github.com/tnakeli/OpenSkiTime';
const image = `${site}/assets/overview.png`;
// [source, route, language, full title, description]
const pages = [
  ['index', '/', 'en', 'OpenSkiTime – Free alpine ski race timing software', 'Free, open-source alpine ski race timing software for Windows: registration, start lists, ALGE timing, FIS points and XML results, PDF reports and live results. Works offline.'],
  ['fi', '/fi/', 'fi', 'OpenSkiTime – Ilmainen alppihiihdon ajanotto-ohjelma', 'Ilmainen avoimen lähdekoodin ajanotto- ja tulospalveluohjelma alppihiihtokilpailuihin: ilmoittautumiset, lähtölistat, ALGE-ajanotto, FIS-pisteet ja XML-tulokset, PDF-raportit ja live-tulokset.'],
  ['download', '/download/', 'en', 'Download OpenSkiTime ski race timing software for Windows', 'Windows downloads, release notes and installation requirements for OpenSkiTime, the free open-source alpine ski race timing software.'],
  ['guide', '/guide/', 'en', 'Your first ski race with OpenSkiTime · Getting started', 'Get started with alpine ski race timing: event series, competitors, start lists, timing, results and optional live publishing.'],
  ['privacy', '/privacy/', 'en', 'Privacy & your race data · OpenSkiTime', 'How local race data and optional online integrations work in OpenSkiTime.'],
  ['code-signing', '/code-signing/', 'en', 'Code signing policy · OpenSkiTime', 'How OpenSkiTime Windows releases are built, approved and signed.'],
  ['404', '/404.html', 'en', 'Page not found · OpenSkiTime', 'Find your way back to OpenSkiTime.']
];
const alternates = { en: '/', fi: '/fi/' };
const text = {
  en: { skip: 'Skip to content', home: 'OpenSkiTime home', nav: 'Main navigation', features: ['/#workflow', 'Features'], guide: 'Get started', download: 'Download', other: ['/fi/', 'fi', 'FI', 'Suomeksi'], tagline: 'Made for the people behind the race.', license: 'Open source · MIT license', privacy: 'Privacy', signing: 'Code signing', licenseLink: 'License', issue: 'Report an issue', vulnerability: 'Report a vulnerability', source: 'Source code' },
  fi: { skip: 'Siirry sisältöön', home: 'OpenSkiTime etusivu', nav: 'Päävalikko', features: ['/fi/#ominaisuudet', 'Ominaisuudet'], guide: 'Opas', download: 'Lataa', other: ['/', 'en', 'EN', 'In English'], tagline: 'Tehty kilpailujen tekijöille.', license: 'Avoin lähdekoodi · MIT-lisenssi', privacy: 'Tietosuoja (en)', signing: 'Koodin allekirjoitus (en)', licenseLink: 'Lisenssi', issue: 'Ilmoita ongelmasta', vulnerability: 'Ilmoita haavoittuvuudesta', source: 'Lähdekoodi' }
};
const header = t => `<a class="skip" href="#main">${t.skip}</a>
<header class="site-header"><div class="shell header-inner"><a class="brand" href="${t === text.fi ? '/fi/' : '/'}" aria-label="${t.home}"><img src="/favicon.svg" width="34" height="34" alt="">OpenSkiTime<span class="brand-dot">.</span></a><nav aria-label="${t.nav}"><a href="${t.features[0]}">${t.features[1]}</a><a href="/guide/">${t.guide}</a><a href="${repo}">GitHub <span aria-hidden="true">↗</span></a><a href="${t.other[0]}" hreflang="${t.other[1]}" lang="${t.other[1]}" title="${t.other[3]}" aria-label="${t.other[3]}">${t.other[2]}</a><a class="nav-download" href="/download/">${t.download} <span aria-hidden="true">↓</span></a></nav></div></header>`;
const footer = t => `<footer class="site-footer"><div class="shell footer-top"><a class="brand" href="${t === text.fi ? '/fi/' : '/'}">OpenSkiTime<span class="brand-dot">.</span></a><p>${t.tagline}</p></div><div class="shell footer-bottom"><span>${t.license}</span><nav aria-label="Footer"><a href="/privacy/">${t.privacy}</a><a href="/code-signing/">${t.signing}</a><a href="${repo}/blob/master/LICENSE">${t.licenseLink}</a><a href="${repo}/issues">${t.issue} ↗</a><a href="${repo}/security/advisories/new">${t.vulnerability} ↗</a><a href="${repo}">${t.source} ↗</a></nav></div></footer>`;

// One source for the visible FAQ and its FAQPage structured data, so search and answer engines read the same text visitors do.
const faq = {
  en: [
    ['What is OpenSkiTime?', 'OpenSkiTime is free, open-source timing and results software for alpine ski races. It runs on Windows and covers the race office from registration and start lists to timing, classification, FIS results, PDF reports and live results.'],
    ['Is OpenSkiTime free?', 'Yes. OpenSkiTime is released under the MIT license. The application, the live timing server and the source code are free to use, change and share.'],
    ['Which timing devices does OpenSkiTime support?', 'OpenSkiTime reads impulses from ALGE Timy over USB, ALGE MT1 over a serial connection and ALGE Results sources. B timing and hand-timing evidence can be collected for the timing report.'],
    ['Does OpenSkiTime work without an internet connection?', 'Yes. Registration, start lists, timing and result preparation work offline on the race computer. FIS lookups, weather and cloud live publishing are optional online features.'],
    ['Can OpenSkiTime produce FIS results?', 'OpenSkiTime calculates run times, rankings, race points and the FIS penalty, and exports TD-approved FIS XML results and the alpine timing report. Submission to FIS is currently limited to test mode.'],
    ['Can spectators follow results live?', 'Yes. OpenSkiTime publishes unofficial live results to a browser view, either from a self-hosted live timing server or from the hosted live.openskiti.me service. Timing capture continues if the connection fails.'],
    ['Is OpenSkiTime ready for official races?', 'OpenSkiTime is in pre-release development. Standard alpine race workflows are implemented. Rehearse with the simulator and use independent backup timing before relying on it at a race.']
  ],
  fi: [
    ['Mikä OpenSkiTime on?', 'OpenSkiTime on ilmainen avoimen lähdekoodin ajanotto- ja tulospalveluohjelma alppihiihtokilpailuihin. Se toimii Windowsissa ja kattaa kilpailutoimiston työt ilmoittautumisista ja lähtölistoista ajanottoon, tuloksiin, FIS-raportointiin, PDF-tulosteisiin ja live-tuloksiin.'],
    ['Onko OpenSkiTime ilmainen?', 'Kyllä. OpenSkiTime on julkaistu MIT-lisenssillä. Ohjelmaa, live-tulospalvelinta ja lähdekoodia saa käyttää, muokata ja jakaa vapaasti.'],
    ['Mitä ajanottolaitteita OpenSkiTime tukee?', 'OpenSkiTime lukee impulssit ALGE Timy -laitteesta USB:n kautta, ALGE MT1:stä sarjaportin kautta sekä ALGE Results -lähteistä. B-ajanoton ja käsiajanoton tiedot voi kerätä ajanottoraporttiin.'],
    ['Toimiiko OpenSkiTime ilman internetyhteyttä?', 'Kyllä. Ilmoittautumiset, lähtölistat, ajanotto ja tulosten valmistelu toimivat kilpailukoneella ilman verkkoyhteyttä. FIS-haut, sääennusteet ja live-tulosten julkaisu pilveen ovat valinnaisia.'],
    ['Tuottaako OpenSkiTime FIS-tulokset?', 'OpenSkiTime laskee laskuajat, sijoitukset, kilpailupisteet ja FIS-penaltyn sekä vie TD:n hyväksymät FIS XML -tulokset ja alppiajanoton raportin. FIS-lähetys toimii toistaiseksi vain testitilassa.'],
    ['Voiko kilpailua seurata livenä?', 'Kyllä. OpenSkiTime julkaisee epäviralliset live-tulokset selaimeen joko omalta palvelimelta tai live.openskiti.me-palvelun kautta. Ajanotto jatkuu, vaikka yhteys katkeaisi.'],
    ['Onko OpenSkiTime valmis virallisiin kilpailuihin?', 'OpenSkiTime on julkaisua edeltävässä kehitysvaiheessa. Tavalliset alppikilpailun työvaiheet on toteutettu. Harjoittele simulaattorilla ja käytä erillistä varajärjestelmää ennen käyttöä kilpailussa.']
  ]
};

let release = null;
if (process.env.OPENSKITIME_RELEASE_FILE) {
  release = JSON.parse(await readFile(process.env.OPENSKITIME_RELEASE_FILE, 'utf8'));
  // Stable vX.Y.Z, or a vX.Y.Z-preview.N prerelease while no stable release exists (scripts/read-release.mjs).
  const tag = release.prerelease ? /^v\d+\.\d+\.\d+-[0-9A-Za-z.-]+$/ : /^v\d+\.\d+\.\d+$/;
  if (release.draft || !tag.test(release.tag_name ?? '')) {
    throw new Error('The website only links to a published application release.');
  }
}
const asset = release?.assets?.find(a => a.name === `OpenSkiTime-${release.tag_name.slice(1)}-win-x64-setup.exe`);
if (release && !asset) throw new Error('Release has no Windows installer.');
const preview = release?.prerelease === true;
if (asset && !asset.browser_download_url.startsWith(`${repo}/releases/download/${release.tag_name}/`)) {
  throw new Error('Unexpected release asset origin.');
}
const escape = value => String(value).replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const download = asset
  ? `<p class="eyebrow">Windows x64 · ${escape(release.tag_name)}${preview ? ' · Preview' : ''}</p><h2>${preview ? 'Try the development preview.' : 'Ready for your next rehearsal.'}</h2><p>${preview ? 'A preview for testing and rehearsals. Do not rely on it alone at an official race; always use independent backup timing. ' : ''}The installer includes the .NET runtime and the local live timing tools.</p><a class="button" href="${escape(asset.browser_download_url)}">Download for Windows <span aria-hidden="true">↓</span></a><p class="small"><a href="${repo}/releases/tag/${escape(release.tag_name)}">Release notes and SHA-256 checksums ↗</a></p><p class="notice">This installer is currently unsigned. Windows may display an unknown-publisher warning. Download only from this project’s GitHub Releases.</p>`
  : `<p class="eyebrow">Pre-release development</p><h2>The first installer is on its way.</h2><p>There is no published Windows installer yet. You can run the current development version from source and follow progress on GitHub.</p><a class="button" href="${repo}/releases">View releases <span aria-hidden="true">↗</span></a><p class="small"><a href="/guide/#from-source">Run from source →</a></p>`;
const faqHtml = lang => `<div class="faq">${faq[lang].map(([q, a]) => `<details><summary>${escape(q)}</summary><p>${escape(a)}</p></details>`).join('')}</div>`;
// JSON-LD is a data block, not a script: browsers do not execute it and the CSP needs no script-src.
const jsonLd = value => `<script type="application/ld+json">${JSON.stringify(value).replace(/</g, '\\u003c')}</script>`;
const software = lang => ({
  '@context': 'https://schema.org',
  '@graph': [
    { '@type': 'WebSite', '@id': `${site}/#website`, url: `${site}/`, name: 'OpenSkiTime', inLanguage: ['en', 'fi'] },
    {
      '@type': 'SoftwareApplication', '@id': `${site}/#software`, name: 'OpenSkiTime', url: `${site}/`,
      description: faq[lang][0][1], inLanguage: lang,
      applicationCategory: 'SportsApplication', applicationSubCategory: 'Alpine ski race timing and results',
      operatingSystem: 'Windows 11', isAccessibleForFree: true, license: `${repo}/blob/master/LICENSE`,
      offers: { '@type': 'Offer', price: '0', priceCurrency: 'EUR' },
      downloadUrl: `${site}/download/`, screenshot: image, image,
      ...(release ? { softwareVersion: release.tag_name.slice(1) } : {}),
      featureList: ['Competitor registration and Excel paste', 'FIS points list lookup', 'Start list draw', 'ALGE Timy, MT1 and ALGE Results timing input', 'Classification with audit history', 'Race points and FIS penalty calculation', 'FIS XML results and timing report', 'PDF start lists and results', 'Live results in the browser', 'Offline race operation'],
      keywords: 'ski race timing software, alpine ski timing, ski racing results, FIS timing, ALGE timing, live results',
      sameAs: [repo]
    },
    { '@type': 'SoftwareSourceCode', codeRepository: repo, programmingLanguage: 'C#', runtimePlatform: '.NET 10', license: `${repo}/blob/master/LICENSE`, targetProduct: { '@id': `${site}/#software` } },
    { '@type': 'FAQPage', mainEntity: faq[lang].map(([q, a]) => ({ '@type': 'Question', name: q, acceptedAnswer: { '@type': 'Answer', text: a } })) }
  ]
});

await mkdir(path.join(output, 'assets'), { recursive: true });
for (const name of ['site.css', 'favicon.svg', 'robots.txt', 'staticwebapp.config.json', 'llms.txt']) {
  await copyFile(path.join(root, name), path.join(output, name));
}
for (const name of ['overview.png', '02-competitions.png', '03-competitors.png', '04-start-lists.png', '05-timing.png', '06-classification.png', '07-race-information.png', '08-timing-report.png', '09-receipt-ocr.png', '10-live-timing.png', '11-pdf-factory.png', '12-referee-report.png']) {
  await copyFile(path.join(root, '..', 'docs', 'screenshots', name), path.join(output, 'assets', name));
}
for (const [name, route, lang, title, description] of pages) {
  const t = text[lang];
  const content = (await readFile(path.join(root, 'pages', `${name}.html`), 'utf8')).replace('{{download}}', download).replace('{{faq}}', faqHtml(lang));
  const translated = Object.values(alternates).includes(route);
  const head = [
    `<title>${escape(title)}</title>`,
    `<meta name="description" content="${escape(description)}">`,
    name === '404' ? `<meta name="robots" content="noindex">` : `<link rel="canonical" href="${site}${route}">`,
    ...(translated ? [...Object.entries(alternates).map(([l, r]) => `<link rel="alternate" hreflang="${l}" href="${site}${r}">`), `<link rel="alternate" hreflang="x-default" href="${site}/">`] : []),
    `<meta name="theme-color" content="#12323c">`,
    `<meta property="og:site_name" content="OpenSkiTime">`,
    `<meta property="og:title" content="${escape(title)}">`,
    `<meta property="og:description" content="${escape(description)}">`,
    `<meta property="og:type" content="website">`,
    `<meta property="og:url" content="${site}${route}">`,
    `<meta property="og:locale" content="${lang === 'fi' ? 'fi_FI' : 'en_US'}">`,
    `<meta property="og:image" content="${image}">`,
    `<meta property="og:image:width" content="1440"><meta property="og:image:height" content="1120">`,
    `<meta property="og:image:alt" content="OpenSkiTime race control with start order, racers on course and ranking">`,
    `<meta name="twitter:card" content="summary_large_image">`,
    `<link rel="icon" href="/favicon.svg" type="image/svg+xml">`,
    `<link rel="stylesheet" href="/site.css">`,
    ...(translated ? [jsonLd(software(lang))] : [])
  ].join('');
  const html = `<!doctype html>\n<html lang="${lang}"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">${head}</head><body>${header(t)}<main id="main">${content}</main>${footer(t)}</body></html>\n`;
  const target = name === 'index' || name === '404' ? output : path.join(output, name);
  await mkdir(target, { recursive: true });
  await writeFile(path.join(target, name === '404' ? '404.html' : 'index.html'), html);
}
// RFC 9116. Each deployment renews the expiry; the contact is GitHub private vulnerability reporting.
const expires = new Date(Date.UTC(new Date().getUTCFullYear() + 1, new Date().getUTCMonth(), new Date().getUTCDate()));
await mkdir(path.join(output, '.well-known'), { recursive: true });
await writeFile(path.join(output, '.well-known', 'security.txt'), `Contact: ${repo}/security/advisories/new\nExpires: ${expires.toISOString().replace('.000Z', 'Z')}\nPolicy: ${repo}/blob/master/SECURITY.md\nPreferred-Languages: en, fi\nCanonical: ${site}/.well-known/security.txt\n`);
const lastmod = new Date().toISOString().slice(0, 10);
await writeFile(path.join(output, 'sitemap.xml'), `<?xml version="1.0" encoding="UTF-8"?><urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">${pages.filter(p => p[0] !== '404').map(p => `<url><loc>${site}${p[1]}</loc><lastmod>${lastmod}</lastmod></url>`).join('')}</urlset>\n`);
console.log(`Built ${pages.length} pages in ${output}; download: ${asset ? release.tag_name : 'pre-release'}.`);
