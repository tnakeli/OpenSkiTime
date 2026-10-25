import { mkdir, readFile, writeFile, copyFile } from 'node:fs/promises';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const root = fileURLToPath(new URL('.', import.meta.url));
const output = path.join(root, 'dist');
const site = 'https://openskiti.me';
const repo = 'https://github.com/tnakeli/OpenSkiTime';
// The maintainer is named on every page and in the structured data so that the project's publisher can be identified.
const maintainer = { name: 'Teemu Niemi', email: 'tniemi@gmail.com', github: 'https://github.com/tnakeli' };
// The project's own social profiles: linked in every footer and listed in the software's sameAs.
const social = [{ name: 'YouTube', url: 'https://www.youtube.com/@OpenSkiTime' }, { name: 'Instagram', url: 'https://www.instagram.com/openskitime/' }];
const image = `${site}/assets/overview.png`;
// Sharing previews use a 1.91:1 card; the structured data keeps the full application screenshot.
const shareImage = `${site}/assets/og-image.png`;
// The videos play on YouTube. The page shows a local poster and links out, so no third-party content loads with the site.
const video = { id: '5m10JrVdtv0', url: 'https://www.youtube.com/watch?v=5m10JrVdtv0', poster: 'demo-video.jpg', uploadDate: '2026-10-25', duration: 'PT2M31S', length: '2:31', name: 'OpenSkiTime demo: a two-run FIS slalom from entries to results', description: 'A complete two-run alpine slalom with 100 synthetic athletes, recorded from the running application: FIS calendar, Excel entries, FIS draw, simulated timing, a referee DSQ, Run 2 start order, live results, FIS XML and PDF reports.' };
const liveVideo = { id: 'W4O_gWhQhyM', url: 'https://www.youtube.com/watch?v=W4O_gWhQhyM', poster: 'live-radio-video.jpg', uploadDate: '2026-10-25', duration: 'PT1M', length: '1:00', name: 'OpenSkiTime live timing with AI race radio', description: 'The live timing control panel and radio settings, then the live race page with the course banner, a DNF and the first finish while two AI hosts comment in Finnish. Four synthetic racers and simulated timing.' };
const radioCredit = 'https://wapice.com/insights/ai-radio-hosts-live-event-commentary/';
// [source, route, language, full title, description]. Descriptions stay under 160 characters so search results show them whole.
const pages = [
  ['index', '/', 'en', 'OpenSkiTime – Free alpine ski race timing software', 'Free, open-source alpine ski race timing software for Windows: start lists, ALGE timing, FIS XML results, PDF reports and live results. Works offline.'],
  ['fi', '/fi/', 'fi', 'OpenSkiTime – Ilmainen alppihiihdon ajanotto-ohjelma', 'Ilmainen avoimen lähdekoodin ajanotto-ohjelma alppihiihtokilpailuihin: lähtölistat, ALGE-ajanotto, FIS-pisteet ja XML-tulokset, PDF-raportit ja live-tulokset.'],
  ['download', '/download/', 'en', 'Download OpenSkiTime ski race timing software for Windows', 'Windows downloads, release notes and installation requirements for OpenSkiTime, the free open-source alpine ski race timing software.'],
  ['guide', '/guide/', 'en', 'Your first ski race with OpenSkiTime · Getting started', 'Get started with alpine ski race timing: event series, competitors, start lists, timing, results and optional live publishing.'],
  ['privacy', '/privacy/', 'en', 'Privacy & your race data · OpenSkiTime', 'How local race data and optional online integrations work in OpenSkiTime.'],
  ['code-signing', '/code-signing/', 'en', 'Code signing policy · OpenSkiTime', 'How OpenSkiTime Windows releases are built, approved and signed.'],
  ['404', '/404.html', 'en', 'Page not found · OpenSkiTime', 'Find your way back to OpenSkiTime.']
];
const alternates = { en: '/', fi: '/fi/' };
// Breadcrumb names for the pages below the English home page.
const crumbs = { download: 'Download', guide: 'Getting started', privacy: 'Privacy', 'code-signing': 'Code signing policy' };
const text = {
  en: { skip: 'Skip to content', home: 'OpenSkiTime home', nav: 'Main navigation', features: ['/#workflow', 'Features'], guide: 'Get started', download: 'Download', other: ['/fi/', 'fi', 'FI', 'Suomeksi'], tagline: 'Made for the people behind the race.', license: 'Open source · MIT license', maintainedBy: 'Maintained by', privacy: 'Privacy', signing: 'Code signing', licenseLink: 'License', issue: 'Report an issue', vulnerability: 'Report a vulnerability', source: 'Source code' },
  fi: { skip: 'Siirry sisältöön', home: 'OpenSkiTime etusivu', nav: 'Päävalikko', features: ['/fi/#ominaisuudet', 'Ominaisuudet'], guide: 'Opas', download: 'Lataa', other: ['/', 'en', 'EN', 'In English'], tagline: 'Tehty kilpailujen tekijöille.', license: 'Avoin lähdekoodi · MIT-lisenssi', maintainedBy: 'Ylläpitäjä', privacy: 'Tietosuoja (en)', signing: 'Koodin allekirjoitus (en)', licenseLink: 'Lisenssi', issue: 'Ilmoita ongelmasta', vulnerability: 'Ilmoita haavoittuvuudesta', source: 'Lähdekoodi' }
};
const header = t => `<a class="skip" href="#main">${t.skip}</a>
<header class="site-header"><div class="shell header-inner"><a class="brand" href="${t === text.fi ? '/fi/' : '/'}" aria-label="${t.home}"><img src="/favicon.svg" width="34" height="34" alt="">OpenSkiTime<span class="brand-dot">.</span></a><nav aria-label="${t.nav}"><a href="${t.features[0]}">${t.features[1]}</a><a href="/guide/">${t.guide}</a><a href="${repo}">GitHub <span aria-hidden="true">↗</span></a><a href="${t.other[0]}" hreflang="${t.other[1]}" lang="${t.other[1]}" title="${t.other[3]}" aria-label="${t.other[3]}">${t.other[2]}</a><a class="nav-download" href="/download/">${t.download} <span aria-hidden="true">↓</span></a></nav></div></header>`;
const footer = t => `<footer class="site-footer"><div class="shell footer-top"><a class="brand" href="${t === text.fi ? '/fi/' : '/'}">OpenSkiTime<span class="brand-dot">.</span></a><p>${t.tagline}</p></div><div class="shell footer-bottom"><span>${t.license} · ${t.maintainedBy} ${maintainer.name}, <a href="mailto:${maintainer.email}">${maintainer.email}</a></span><nav aria-label="Footer"><a href="/privacy/">${t.privacy}</a><a href="/code-signing/">${t.signing}</a><a href="${repo}/blob/master/LICENSE">${t.licenseLink}</a><a href="${repo}/issues">${t.issue} ↗</a><a href="${repo}/security/advisories/new">${t.vulnerability} ↗</a><a href="${repo}">${t.source} ↗</a>${social.map(s => `<a href="${s.url}">${s.name} ↗</a>`).join('')}</nav></div></footer>`;

// One source for the visible FAQ and its FAQPage structured data, so search and answer engines read the same text visitors do.
const faq = {
  en: [
    ['What is OpenSkiTime?', 'OpenSkiTime is free, open-source timing and results software for alpine ski races. It runs on Windows and covers the race office from registration and start lists to timing, classification, FIS results, PDF reports and live results.'],
    ['Is OpenSkiTime free?', 'Yes. OpenSkiTime is released under the MIT license. The application, the live timing server and the source code are free to use, change and share.'],
    ['Which timing devices does OpenSkiTime support?', 'OpenSkiTime reads impulses from ALGE Timy over USB, ALGE MT1 over a serial connection and ALGE Results sources. B timing and hand-timing evidence can be collected for the timing report.'],
    ['Does OpenSkiTime work without an internet connection?', 'Yes. Registration, start lists, timing and result preparation work offline on the race computer. FIS lookups, weather and cloud live publishing are optional online features.'],
    ['Can OpenSkiTime produce FIS results?', 'OpenSkiTime calculates run times, rankings, race points and the FIS penalty, and exports TD-approved FIS XML results and the alpine timing report. Submission to FIS is currently limited to test mode.'],
    ['Can spectators follow results live?', 'Yes. OpenSkiTime publishes unofficial live results to a browser view, either from a self-hosted live timing server or from the hosted live.openskiti.me service. Timing capture continues if the connection fails.'],
    ['Is OpenSkiTime ready for official races?', 'Not yet. OpenSkiTime is in pre-release development and is not yet intended for timing real races. Standard alpine race workflows are implemented and will be tested at real competitions during the 2026/27 season. Rehearse with the simulator and use independent backup timing.']
  ],
  fi: [
    ['Mikä OpenSkiTime on?', 'OpenSkiTime on ilmainen avoimen lähdekoodin ajanotto- ja tulospalveluohjelma alppihiihtokilpailuihin. Se toimii Windowsissa ja kattaa kilpailutoimiston työt ilmoittautumisista ja lähtölistoista ajanottoon, tuloksiin, FIS-raportointiin, PDF-tulosteisiin ja live-tuloksiin.'],
    ['Onko OpenSkiTime ilmainen?', 'Kyllä. OpenSkiTime on julkaistu MIT-lisenssillä. Ohjelmaa, live-tulospalvelinta ja lähdekoodia saa käyttää, muokata ja jakaa vapaasti.'],
    ['Mitä ajanottolaitteita OpenSkiTime tukee?', 'OpenSkiTime lukee impulssit ALGE Timy -laitteesta USB:n kautta, ALGE MT1:stä sarjaportin kautta sekä ALGE Results -lähteistä. B-ajanoton ja käsiajanoton tiedot voi kerätä ajanottoraporttiin.'],
    ['Toimiiko OpenSkiTime ilman internetyhteyttä?', 'Kyllä. Ilmoittautumiset, lähtölistat, ajanotto ja tulosten valmistelu toimivat kilpailukoneella ilman verkkoyhteyttä. FIS-haut, sääennusteet ja live-tulosten julkaisu pilveen ovat valinnaisia.'],
    ['Tuottaako OpenSkiTime FIS-tulokset?', 'OpenSkiTime laskee laskuajat, sijoitukset, kilpailupisteet ja FIS-penaltyn sekä vie TD:n hyväksymät FIS XML -tulokset ja alppiajanoton raportin. FIS-lähetys toimii toistaiseksi vain testitilassa.'],
    ['Voiko kilpailua seurata livenä?', 'Kyllä. OpenSkiTime julkaisee epäviralliset live-tulokset selaimeen joko omalta palvelimelta tai live.openskiti.me-palvelun kautta. Ajanotto jatkuu, vaikka yhteys katkeaisi.'],
    ['Onko OpenSkiTime valmis virallisiin kilpailuihin?', 'Ei vielä. OpenSkiTime on julkaisua edeltävässä kehitysvaiheessa, eikä sitä ole vielä tarkoitettu oikeiden kilpailujen ajanottoon. Tavalliset alppikilpailun työvaiheet on toteutettu, ja niitä testataan oikeissa kilpailuissa kaudella 2026/27. Harjoittele simulaattorilla ja käytä erillistä varajärjestelmää.']
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
// Releases made before signing was enabled say so in their notes; the notice must describe the file actually offered.
const unsigned = /\*\*Unsigned build:\*\*/.test(release?.body ?? '');
if (asset && !asset.browser_download_url.startsWith(`${repo}/releases/download/${release.tag_name}/`)) {
  throw new Error('Unexpected release asset origin.');
}
// A page changes when its source, the shared builder or (for the download page) the offered release changes.
// The website workflow checks out full history; without git the sitemap omits lastmod rather than guessing.
const gitDate = file => { try { return execFileSync('git', ['log', '-1', '--format=%cs', '--', file], { cwd: root, encoding: 'utf8' }).trim() || null; } catch { return null; } };
const builderDate = gitDate('build.mjs');
const lastmod = name => {
  const dates = [gitDate(`pages/${name}.html`), builderDate, name === 'download' ? release?.published_at?.slice(0, 10) : null].filter(Boolean);
  return builderDate ? dates.sort().at(-1) : null;
};
const escape = value => String(value).replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const download = asset
  ? `<p class="eyebrow">Windows x64 · ${escape(release.tag_name)}${preview ? ' · Preview' : ''}</p><h2>${preview ? 'Try the development preview.' : 'Ready for your next rehearsal.'}</h2><p>${preview ? 'A preview for evaluation and rehearsals. OpenSkiTime is still in development and is not yet intended for timing real races. It will be tested at real competitions during the 2026/27 season. Always use independent backup timing. ' : ''}The installer includes the .NET runtime and the local live timing tools.</p><a class="button" href="${escape(asset.browser_download_url)}">Download for Windows <span aria-hidden="true">↓</span></a><p class="small"><a href="${repo}/releases/tag/${escape(release.tag_name)}">Release notes and SHA-256 checksums ↗</a></p><p class="notice">${unsigned ? 'This installer is unsigned. Windows may display an unknown-publisher warning.' : 'The installer is digitally signed by Teemu Niemi (Certum code signing certificate). Windows SmartScreen may still warn about a new download until it has built a reputation.'} Download only from this project’s GitHub Releases.</p>`
  : `<p class="eyebrow">Pre-release development</p><h2>The first installer is on its way.</h2><p>There is no published Windows installer yet. You can run the current development version from source and follow progress on GitHub.</p><a class="button" href="${repo}/releases">View releases <span aria-hidden="true">↗</span></a><p class="small"><a href="/guide/#from-source">Run from source →</a></p>`;
const videoText = {
  en: { eyebrow: 'SEE A WHOLE RACE', heading: 'A two-run slalom.<br>In two and a half minutes.', intro: 'From the FIS calendar and an Excel entry list to the draw, timing, a referee DSQ, live results, FIS XML and PDF reports. Recorded from the running application; captions explain each step.', bar: 'OpenSkiTime / Demo', watch: 'Watch on YouTube', alt: 'Play the OpenSkiTime demo video on YouTube: combined Run 2 ranking of a fictional women’s slalom', caption: 'Opens YouTube. 100 synthetic athletes and simulated timing; no sound.' },
  fi: { eyebrow: 'KATSO KOKO KILPAILU', heading: 'Kahden kierroksen pujottelu<br>kahdessa ja puolessa minuutissa.', intro: 'FIS-kalenterista ja Excelin osallistujalistasta arvontaan, ajanottoon, tuomarin hylkäykseen, live-tuloksiin, FIS XML:ään ja PDF-raportteihin. Tallennettu käynnissä olevasta ohjelmasta; englanninkieliset tekstit selittävät vaiheet.', bar: 'OpenSkiTime / Esittely', watch: 'Katso YouTubessa', alt: 'Toista OpenSkiTime-esittelyvideo YouTubessa: kuvitteellisen naisten pujottelun yhdistetyt tulokset toisen kierroksen jälkeen', caption: 'Avautuu YouTubeen. 100 kuvitteellista urheilijaa ja simuloitu ajanotto; ei ääntä.' }
};
const liveVideoText = {
  en: { eyebrow: 'LIVE TIMING WITH RACE RADIO', heading: 'Live results.<br>With a voice.', intro: 'Viewers follow the race in any browser: the next racers, everyone on course and the latest finish. Press Radio and two AI hosts comment on starts, finishes and DNFs in Finnish within seconds. The organizer turns the radio on in the live timing control panel.', bar: 'OpenSkiTime / Live timing', watch: 'Watch on YouTube', alt: 'Play the OpenSkiTime live timing video on YouTube: live race page with the course banner, a DNF and the first finish of a fictional slalom', caption: 'Opens YouTube. Four synthetic racers and simulated timing; AI-generated commentary in Finnish, with sound.', credit: `Race radio: thanks &amp; inspired by Lassi — <a href="${radioCredit}">AI radio hosts for live event commentary ↗</a>` },
  fi: { eyebrow: 'LIVE-TULOKSET JA KILPAILURADIO', heading: 'Live-tulokset.<br>Äänen kanssa.', intro: 'Yleisö seuraa kilpailua selaimessa: seuraavat lähtijät, radalla olevat ja viimeisin maaliintulo. Radio-painikkeesta kaksi tekoälyjuontajaa kommentoi lähdöt, maaliintulot ja keskeytykset suomeksi sekunneissa. Järjestäjä kytkee radion päälle live timingin ohjauspaneelista.', bar: 'OpenSkiTime / Live-tulokset', watch: 'Katso YouTubessa', alt: 'Toista OpenSkiTimen live-tulosvideo YouTubessa: kuvitteellisen pujottelun live-sivu, keskeytys ja ensimmäinen maaliintulo', caption: 'Avautuu YouTubeen. Neljä kuvitteellista laskijaa ja simuloitu ajanotto; tekoälyn suomenkielinen selostus, äänellä.', credit: `Kilpailuradio: kiitos ja inspiraatio Lassille — <a href="${radioCredit}">AI radio hosts for live event commentary ↗</a>` }
};
const videoSection = (item, v) => `<div class="section-heading"><div><p class="eyebrow">${v.eyebrow}</p><h2>${v.heading}</h2></div><p>${v.intro}</p></div><figure class="product-shot"><div class="window-bar"><span></span><span></span><span></span><p>${v.bar}</p><b>${item.length}</b></div><a class="video-link" href="${item.url}"><img src="/assets/${item.poster}" width="1440" height="810" alt="${escape(v.alt)}" loading="lazy"><span class="play-button">${v.watch} <span aria-hidden="true">▶</span></span></a><figcaption>${v.caption}</figcaption></figure>${v.credit ? `<p class="small">${v.credit}</p>` : ''}`;
const videoHtml = lang => videoSection(video, videoText[lang]);
const liveVideoHtml = lang => videoSection(liveVideo, liveVideoText[lang]);
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
      author: { '@id': `${site}/#maintainer` }, maintainer: { '@id': `${site}/#maintainer` },
      sameAs: [repo, ...social.map(s => s.url)]
    },
    { '@type': 'Person', '@id': `${site}/#maintainer`, name: maintainer.name, email: `mailto:${maintainer.email}`, url: maintainer.github, sameAs: [maintainer.github] },
    { '@type': 'SoftwareSourceCode', codeRepository: repo, programmingLanguage: 'C#', runtimePlatform: '.NET 10', license: `${repo}/blob/master/LICENSE`, targetProduct: { '@id': `${site}/#software` } },
    ...[[video, 'demo-video'], [liveVideo, 'live-video']].map(([v, id]) => ({ '@type': 'VideoObject', '@id': `${site}/#${id}`, name: v.name, description: v.description, inLanguage: 'en', thumbnailUrl: `${site}/assets/${v.poster}`, uploadDate: v.uploadDate, duration: v.duration, url: v.url, embedUrl: `https://www.youtube-nocookie.com/embed/${v.id}`, about: { '@id': `${site}/#software` } })),
    { '@type': 'FAQPage', mainEntity: faq[lang].map(([q, a]) => ({ '@type': 'Question', name: q, acceptedAnswer: { '@type': 'Answer', text: a } })) }
  ]
});
const subpage = (name, route, title, description) => ({
  '@context': 'https://schema.org',
  '@graph': [
    { '@type': 'BreadcrumbList', itemListElement: [
      { '@type': 'ListItem', position: 1, name: 'OpenSkiTime', item: `${site}/` },
      { '@type': 'ListItem', position: 2, name: crumbs[name], item: `${site}${route}` }
    ] },
    ...(name === 'guide' ? [{
      '@type': 'TechArticle', '@id': `${site}${route}#article`, headline: title, description, inLanguage: 'en', url: `${site}${route}`,
      image: `${site}/assets/05-timing.png`, author: { '@type': 'Person', name: maintainer.name, url: maintainer.github },
      about: { '@type': 'SoftwareApplication', '@id': `${site}/#software`, name: 'OpenSkiTime' },
      ...(lastmod(name) ? { dateModified: lastmod(name) } : {})
    }] : [])
  ]
});

await mkdir(path.join(output, 'assets'), { recursive: true });
for (const name of ['site.css', 'favicon.svg', 'favicon.ico', 'apple-touch-icon.png', 'robots.txt', 'staticwebapp.config.json', 'indexnow-key.txt']) {
  await copyFile(path.join(root, name), path.join(output, name));
}
for (const name of [video.poster, liveVideo.poster, 'og-image.png', 'overview.png', '02-competitions.png', '03-competitors.png', '04-start-lists.png', '05-timing.png', '06-classification.png', '07-race-information.png', '08-timing-report.png', '09-receipt-ocr.png', '10-live-timing.png', '11-pdf-factory.png', '12-referee-report.png']) {
  await copyFile(path.join(root, '..', 'docs', 'screenshots', name), path.join(output, 'assets', name));
}
for (const [name, route, lang, title, description] of pages) {
  const t = text[lang];
  const content = (await readFile(path.join(root, 'pages', `${name}.html`), 'utf8')).replace('{{download}}', download).replace('{{faq}}', faqHtml(lang)).replace('{{video}}', videoHtml(lang)).replace('{{liveVideo}}', liveVideoHtml(lang));
  const translated = Object.values(alternates).includes(route);
  const head = [
    `<title>${escape(title)}</title>`,
    `<meta name="description" content="${escape(description)}">`,
    name === '404' ? `<meta name="robots" content="noindex">` : `<link rel="canonical" href="${site}${route}">`,
    ...(translated ? [...Object.entries(alternates).map(([l, r]) => `<link rel="alternate" hreflang="${l}" href="${site}${r}">`), `<link rel="alternate" hreflang="x-default" href="${site}/">`] : []),
    `<meta name="theme-color" content="#14234a">`,
    `<meta property="og:site_name" content="OpenSkiTime">`,
    `<meta property="og:title" content="${escape(title)}">`,
    `<meta property="og:description" content="${escape(description)}">`,
    `<meta property="og:type" content="website">`,
    `<meta property="og:url" content="${site}${route}">`,
    `<meta property="og:locale" content="${lang === 'fi' ? 'fi_FI' : 'en_US'}">`,
    ...(translated ? [`<meta property="og:locale:alternate" content="${lang === 'fi' ? 'en_US' : 'fi_FI'}">`] : []),
    `<meta property="og:image" content="${shareImage}">`,
    `<meta property="og:image:width" content="1200"><meta property="og:image:height" content="630">`,
    `<meta property="og:image:alt" content="OpenSkiTime, free alpine ski race timing software, with its race control screen">`,
    `<meta name="twitter:card" content="summary_large_image">`,
    `<link rel="icon" href="/favicon.ico" sizes="32x32"><link rel="icon" href="/favicon.svg" type="image/svg+xml"><link rel="apple-touch-icon" href="/apple-touch-icon.png">`,
    `<link rel="stylesheet" href="/site.css">`,
    ...(translated ? [jsonLd(software(lang))] : crumbs[name] ? [jsonLd(subpage(name, route, title, description))] : [])
  ].join('');
  const html = `<!doctype html>\n<html lang="${lang}"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">${head}</head><body>${header(t)}<main id="main">${content}</main>${footer(t)}</body></html>\n`;
  const target = name === 'index' || name === '404' ? output : path.join(output, name);
  await mkdir(target, { recursive: true });
  await writeFile(path.join(target, name === '404' ? '404.html' : 'index.html'), html);
}
// RFC 9116. Each deployment renews the expiry; contacts in order of preference: GitHub private vulnerability reporting, then the maintainer's email.
const expires = new Date(Date.UTC(new Date().getUTCFullYear() + 1, new Date().getUTCMonth(), new Date().getUTCDate()));
await mkdir(path.join(output, '.well-known'), { recursive: true });
await writeFile(path.join(output, '.well-known', 'security.txt'), `Contact: ${repo}/security/advisories/new\nContact: mailto:${maintainer.email}\nExpires: ${expires.toISOString().replace('.000Z', 'Z')}\nPolicy: ${repo}/blob/master/SECURITY.md\nPreferred-Languages: en, fi\nCanonical: ${site}/.well-known/security.txt\n`);
await writeFile(path.join(output, 'sitemap.xml'), `<?xml version="1.0" encoding="UTF-8"?><urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">${pages.filter(p => p[0] !== '404').map(([name, route]) => `<url><loc>${site}${route}</loc>${lastmod(name) ? `<lastmod>${lastmod(name)}</lastmod>` : ''}</url>`).join('')}</urlset>\n`);
// llms.txt states the release the website currently offers; llms-full.txt adds the FAQ and guide text for tools that read one file.
const releaseLine = asset
  ? `- Current release: ${release.tag_name}${preview ? ' (development preview)' : ''}; Windows installer: ${asset.browser_download_url}`
  : '- Current release: no published Windows installer yet; run from source (https://openskiti.me/guide/#from-source)';
const llms = (await readFile(path.join(root, 'llms.txt'), 'utf8')).replace('{{release}}', releaseLine);
await writeFile(path.join(output, 'llms.txt'), llms);
const plain = html => html.replace(/<pre><code>([\s\S]*?)<\/code><\/pre>/g, (_, code) => `\n\`\`\`\n${code}\n\`\`\`\n`).replace(/<a href="([^"]+)">([^<]*)<\/a>/g, '[$2]($1)').replace(/<[^>]+>/g, '').replace(/&amp;/g, '&').replace(/&lt;/g, '<').replace(/&gt;/g, '>').trim();
const guideSource = await readFile(path.join(root, 'pages', 'guide.html'), 'utf8');
const guideText = [...guideSource.matchAll(/<article id="[^"]+">[\s\S]*?<h2>([\s\S]*?)<\/h2>([\s\S]*?)<\/article>/g)]
  .map(([, heading, body]) => `### ${plain(heading)}\n\n${[...body.matchAll(/<p>([\s\S]*?)<\/p>|<pre>[\s\S]*?<\/pre>/g)].map(m => plain(m[1] ?? m[0])).join('\n\n')}`).join('\n\n');
const faqText = lang => faq[lang].map(([q, a]) => `### ${q}\n\n${a}`).join('\n\n');
await writeFile(path.join(output, 'llms-full.txt'), `${llms.trimEnd()}\n\n## Frequently asked questions\n\n${faqText('en')}\n\n## Usein kysytyt kysymykset (suomeksi)\n\n${faqText('fi')}\n\n## Getting started guide\n\nSource: https://openskiti.me/guide/\n\n${guideText}\n`);
console.log(`Built ${pages.length} pages in ${output}; download: ${asset ? release.tag_name : 'pre-release'}.`);
