// @ts-check

/**
 * Documentation sidebar. Ordered as a first-time reader would progress:
 * the two calls -> a full run start to finish -> whole tasks -> how the file
 * works -> reference.
 *
 * @type {import('@docusaurus/plugin-content-docs').SidebarsConfig}
 */
const sidebars = {
  docsSidebar: [
    'intro',
    'getting-started',
    'cli',
    {
      type: 'category',
      label: 'Recipes',
      collapsed: false,
      items: [
        'masked-slice-for-dev',
        'repro-a-customer-bug',
        'slice-with-schema',
        'hand-it-to-an-agent',
      ],
    },
    {
      type: 'category',
      label: 'How it works',
      collapsed: false,
      items: [
        'package-format',
        'editing-the-package',
        'importing',
      ],
    },
    {
      type: 'category',
      label: 'Reference',
      collapsed: false,
      items: [
        'options',
        'transformations',
        'supported-types',
        'comparison',
        'known-limitations',
        'troubleshooting',
        'support-matrix',
        'versioning',
        'changelog',
        'faq',
      ],
    },
  ],
};

module.exports = sidebars;
