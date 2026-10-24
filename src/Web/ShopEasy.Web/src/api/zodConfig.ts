import { z } from 'zod'

// Zod makes object parsing faster by compiling a parser with `new Function`. The gateway's
// Content-Security-Policy does not allow that (scripts only from its own files, no 'unsafe-eval'), and
// Zod finds out by trying: the browser blocks it and reports a violation in the console on every page
// load. So it is switched off here, once, before any schema exists (main.tsx imports this first). The
// cost is microseconds for the few small objects the app parses.
z.config({ jitless: true })
