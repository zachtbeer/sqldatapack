// @ts-check
// Docusaurus configuration for the SqlDataPack documentation site.
// See https://docusaurus.io/docs/api/docusaurus-config

const { themes } = require('prism-react-renderer');

/** @type {import('@docusaurus/types').Config} */
const config = {
  title: 'SqlDataPack',
  tagline: 'Export the SQL Server data you choose into one portable SQLite file, edit it, then import it back.',
  favicon: 'img/icon.png',

  // Project site: https://zachtbeer.github.io/sqldatapack/
  url: 'https://zachtbeer.github.io',
  baseUrl: '/sqldatapack/',

  organizationName: 'zachtbeer',
  projectName: 'sqldatapack',

  onBrokenLinks: 'throw',

  markdown: {
    mermaid: true,
    hooks: {
      onBrokenMarkdownLinks: 'throw',
    },
  },

  themes: ['@docusaurus/theme-mermaid'],

  i18n: {
    defaultLocale: 'en',
    locales: ['en'],
  },

  presets: [
    [
      'classic',
      /** @type {import('@docusaurus/preset-classic').Options} */
      ({
        docs: {
          // Serve the docs at the site root; intro.md is the homepage.
          routeBasePath: '/',
          sidebarPath: require.resolve('./sidebars.js'),
          editUrl: 'https://github.com/zachtbeer/sqldatapack/edit/main/website/',
        },
        blog: false,
        theme: {
          customCss: require.resolve('./src/css/custom.css'),
        },
      }),
    ],
  ],

  themeConfig:
    /** @type {import('@docusaurus/preset-classic').ThemeConfig} */
    ({
      image: 'img/icon.png',
      colorMode: {
        defaultMode: 'dark',
        respectPrefersColorScheme: true,
      },
      navbar: {
        title: 'SqlDataPack',
        logo: {
          alt: 'SqlDataPack',
          src: 'img/icon.png',
        },
        items: [
          {
            type: 'docSidebar',
            sidebarId: 'docsSidebar',
            position: 'left',
            label: 'Docs',
          },
          {
            href: 'https://www.nuget.org/packages/SqlDataPack',
            label: 'NuGet',
            position: 'right',
          },
          {
            href: 'https://github.com/zachtbeer/sqldatapack',
            label: 'GitHub',
            position: 'right',
          },
        ],
      },
      footer: {
        style: 'dark',
        links: [
          {
            title: 'Docs',
            items: [
              { label: 'Getting Started', to: '/getting-started' },
              { label: 'Options', to: '/options' },
              { label: 'Troubleshooting', to: '/troubleshooting' },
              { label: 'FAQ', to: '/faq' },
            ],
          },
          {
            title: 'Project',
            items: [
              { label: 'GitHub', href: 'https://github.com/zachtbeer/sqldatapack' },
              { label: 'NuGet', href: 'https://www.nuget.org/packages/SqlDataPack' },
              { label: 'Changelog', to: '/changelog' },
            ],
          },
        ],
        copyright: `Copyright © ${new Date().getFullYear()} zachtbeer. Licensed under MIT.`,
      },
      prism: {
        theme: themes.github,
        darkTheme: themes.dracula,
        additionalLanguages: ['csharp', 'sql', 'bash'],
      },
    }),
};

module.exports = config;
