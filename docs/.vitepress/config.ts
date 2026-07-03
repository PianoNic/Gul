import { defineConfig } from 'vitepress'

export default defineConfig({
  title: 'Gul',
  description: 'A minimal ngrok-style devtunnel. Instant public HTTPS URLs for anything on your localhost.',
  lastUpdated: true,
  cleanUrls: true,
  ignoreDeadLinks: true,
  head: [
    ['link', { rel: 'icon', href: '/favicon.svg' }],
  ],
  themeConfig: {
    logo: '/logo.svg',
    nav: [
      { text: 'Intro', link: '/intro' },
      { text: 'Self-host', link: '/self-host' },
      { text: 'CLI', link: '/cli' },
      { text: 'Development', link: '/dev-setup' },
    ],
    sidebar: [
      { text: 'What is Gul?', link: '/intro' },
      {
        text: 'Setup',
        collapsed: false,
        items: [
          { text: 'Self-hosting', link: '/self-host' },
          { text: 'CLI client', link: '/cli' },
        ],
      },
      {
        text: 'Development',
        collapsed: false,
        items: [
          { text: 'Developer setup', link: '/dev-setup' },
        ],
      },
    ],
    socialLinks: [
      { icon: 'github', link: 'https://github.com/PianoNic/Gul' },
    ],
    search: { provider: 'local' },
    editLink: {
      pattern: 'https://github.com/PianoNic/Gul/edit/main/docs/:path',
      text: 'Edit this page on GitHub',
    },
    footer: {
      message: 'Made with care by PianoNic.',
      copyright: 'Gul',
    },
  },
})
