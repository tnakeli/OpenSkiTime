/* Nations of the unofficial live view. Racers carry the three-letter FIS nation code (IOC style: GER, SUI, NED); the flag
   is the Unicode regional-indicator pair of the matching ISO 3166 code, so no images are downloaded. A code that is not
   listed shows without flag, under its own code. */
(() => {
  const nations = {
    AFG: ['AF', 'Afghanistan'], ALB: ['AL', 'Albania'], ALG: ['DZ', 'Algeria'], AND: ['AD', 'Andorra'], ANG: ['AO', 'Angola'],
    ARG: ['AR', 'Argentina'], ARM: ['AM', 'Armenia'], ASA: ['AS', 'American Samoa'], AUS: ['AU', 'Australia'], AUT: ['AT', 'Austria'],
    AZE: ['AZ', 'Azerbaijan'], BAH: ['BS', 'Bahamas'], BAN: ['BD', 'Bangladesh'], BAR: ['BB', 'Barbados'], BEL: ['BE', 'Belgium'],
    BER: ['BM', 'Bermuda'], BHU: ['BT', 'Bhutan'], BIH: ['BA', 'Bosnia and Herzegovina'], BLR: ['BY', 'Belarus'], BOL: ['BO', 'Bolivia'],
    BRA: ['BR', 'Brazil'], BUL: ['BG', 'Bulgaria'], CAN: ['CA', 'Canada'], CAY: ['KY', 'Cayman Islands'], CHI: ['CL', 'Chile'],
    CHN: ['CN', 'China'], CMR: ['CM', 'Cameroon'], COL: ['CO', 'Colombia'], CRC: ['CR', 'Costa Rica'], CRO: ['HR', 'Croatia'],
    CYP: ['CY', 'Cyprus'], CZE: ['CZ', 'Czechia'], DEN: ['DK', 'Denmark'], DOM: ['DO', 'Dominican Republic'], ECU: ['EC', 'Ecuador'],
    EGY: ['EG', 'Egypt'], ERI: ['ER', 'Eritrea'], ESA: ['SV', 'El Salvador'], ESP: ['ES', 'Spain'], EST: ['EE', 'Estonia'],
    ETH: ['ET', 'Ethiopia'], FIJ: ['FJ', 'Fiji'], FIN: ['FI', 'Finland'], FRA: ['FR', 'France'], GBR: ['GB', 'Great Britain'],
    GEO: ['GE', 'Georgia'], GER: ['DE', 'Germany'], GHA: ['GH', 'Ghana'], GRE: ['GR', 'Greece'], GUA: ['GT', 'Guatemala'],
    GUM: ['GU', 'Guam'], HAI: ['HT', 'Haiti'], HKG: ['HK', 'Hong Kong, China'], HON: ['HN', 'Honduras'], HUN: ['HU', 'Hungary'],
    INA: ['ID', 'Indonesia'], IND: ['IN', 'India'], IRI: ['IR', 'Iran'], IRL: ['IE', 'Ireland'], IRQ: ['IQ', 'Iraq'],
    ISL: ['IS', 'Iceland'], ISR: ['IL', 'Israel'], ISV: ['VI', 'Virgin Islands, US'], ITA: ['IT', 'Italy'], IVB: ['VG', 'British Virgin Islands'],
    JAM: ['JM', 'Jamaica'], JOR: ['JO', 'Jordan'], JPN: ['JP', 'Japan'], KAZ: ['KZ', 'Kazakhstan'], KEN: ['KE', 'Kenya'],
    KGZ: ['KG', 'Kyrgyzstan'], KOR: ['KR', 'Korea'], KOS: ['XK', 'Kosovo'], KSA: ['SA', 'Saudi Arabia'], KUW: ['KW', 'Kuwait'],
    LAT: ['LV', 'Latvia'], LBN: ['LB', 'Lebanon'], LIE: ['LI', 'Liechtenstein'], LTU: ['LT', 'Lithuania'], LUX: ['LU', 'Luxembourg'],
    MAD: ['MG', 'Madagascar'], MAR: ['MA', 'Morocco'], MAS: ['MY', 'Malaysia'], MDA: ['MD', 'Moldova'], MEX: ['MX', 'Mexico'],
    MGL: ['MN', 'Mongolia'], MKD: ['MK', 'North Macedonia'], MLT: ['MT', 'Malta'], MNE: ['ME', 'Montenegro'], MON: ['MC', 'Monaco'],
    NED: ['NL', 'Netherlands'], NEP: ['NP', 'Nepal'], NGR: ['NG', 'Nigeria'], NOR: ['NO', 'Norway'], NZL: ['NZ', 'New Zealand'],
    PAK: ['PK', 'Pakistan'], PER: ['PE', 'Peru'], PHI: ['PH', 'Philippines'], POL: ['PL', 'Poland'], POR: ['PT', 'Portugal'],
    PRK: ['KP', 'DPR Korea'], PUR: ['PR', 'Puerto Rico'], QAT: ['QA', 'Qatar'], ROU: ['RO', 'Romania'], RSA: ['ZA', 'South Africa'],
    RUS: ['RU', 'Russia'], SEN: ['SN', 'Senegal'], SGP: ['SG', 'Singapore'], SLO: ['SI', 'Slovenia'], SMR: ['SM', 'San Marino'],
    SRB: ['RS', 'Serbia'], SRI: ['LK', 'Sri Lanka'], SUI: ['CH', 'Switzerland'], SVK: ['SK', 'Slovakia'], SWE: ['SE', 'Sweden'],
    THA: ['TH', 'Thailand'], TJK: ['TJ', 'Tajikistan'], TLS: ['TL', 'Timor-Leste'], TGA: ['TO', 'Tonga'], TPE: ['TW', 'Chinese Taipei'],
    TTO: ['TT', 'Trinidad and Tobago'], TUN: ['TN', 'Tunisia'], TUR: ['TR', 'Türkiye'], UAE: ['AE', 'United Arab Emirates'], UKR: ['UA', 'Ukraine'],
    URU: ['UY', 'Uruguay'], USA: ['US', 'United States'], UZB: ['UZ', 'Uzbekistan'], VEN: ['VE', 'Venezuela'], VIE: ['VN', 'Vietnam'],
    ZIM: ['ZW', 'Zimbabwe']
  };
  // Two regional indicator symbols (U+1F1E6 is A) make a flag where the platform has flag glyphs.
  const flag = iso => String.fromCodePoint(...[...iso].map(c => 0x1F1E6 + c.charCodeAt(0) - 65));
  /** The flag, display code and name of a nation code; flag is '' for an unknown or blank code. */
  globalThis.liveNation = code => {
    const key = String(code || '').trim().toUpperCase(), known = nations[key];
    return { code: key, flag: known ? flag(known[0]) : '', name: known ? known[1] : key };
  };
})();
