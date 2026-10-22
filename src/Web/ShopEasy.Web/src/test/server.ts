import { setupServer } from 'msw/node'

// The fake gateway. It answers real fetch calls (the app code runs exactly as in the browser); a
// test says how with server.use(...), see test/gateway.ts.
export const server = setupServer()
