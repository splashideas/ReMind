describe('mobile scaffold', () => {
  it('declares the redirect scheme used by the public client', () => {
    const appConfig = require('./app.json');

    expect(appConfig.expo.scheme).toBe('remind-mobile');
  });
});
