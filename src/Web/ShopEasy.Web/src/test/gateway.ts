import { http, HttpResponse } from 'msw'

export const alice = {
  isAuthenticated: true,
  userId: 'u-1',
  email: 'alice@example.com',
  name: 'Alice',
  roles: ['customer'],
}

// How the gateway answers /account/me
export const nobodyIsSignedIn = () => http.get('/account/me', () => new HttpResponse(null, { status: 401 }))

export const signedInAs = (user: object = alice) => http.get('/account/me', () => HttpResponse.json(user))
