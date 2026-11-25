// Polyfill do atributo popover para navegadores antigos (carregado só quando necessário).
if (!Object.hasOwn(HTMLElement.prototype, 'popover')) {
  import('https://unpkg.com/@oddbird/popover-polyfill@latest/dist/popover.min.js');
}
