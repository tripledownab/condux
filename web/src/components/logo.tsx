// The square Condux mark (the C + AI glyph), inlined like the logotype so it inherits the current
// text color and adapts to the theme. For icon-sized brand spots: the collapsed sidebar, mobile chrome.
export function LogoMark({ className, title = "Condux" }: { className?: string; title?: string }) {
  return (
    <svg
      viewBox="0 0 36.9 36.9"
      className={className}
      fill="currentColor"
      role="img"
      aria-label={title}
    >
      <title>{title}</title>
      <path d="M7.1 27.3V8.8l4.5-4.5h13.5l4.5 4.5V13h-5.4v-2.3l-1.9-1.9h-7.9l-2 2v14.5l2 2h7.9l1.9-1.9v-2.3h5.4v4.2l-4.5 4.5H11.7l-4.6-4.5z" />
      <path d="M25.7 15.9h.8l1.7 4.4h-.9l-.4-1h-1.7l-.4 1h-.9l1.8-4.4zm1 2.7-.6-1.7-.6 1.7h1.2zM28.7 15.9h.9v4.4h-.9v-4.4z" />
    </svg>
  );
}

// The Condux logotype, inlined from @condux/brand so it inherits the current text color
// (fill=currentColor) and therefore adapts to the theme. Size it via className (e.g. `h-6 w-auto`).
export function Logo({ className, title = "Condux" }: { className?: string; title?: string }) {
  return (
    <svg
      viewBox="0 0 144.1 37.9"
      className={className}
      fill="currentColor"
      role="img"
      aria-label={title}
    >
      <title>{title}</title>
      <path d="M133.4 3.8h1.5l3 7.8h-1.6l-.7-1.7h-2.9l-.7 1.7h-1.6l3-7.8zm1.9 4.8-1.1-2.9-1.1 2.9h2.2z" />
      <path d="M138.7 3.8h1.6v7.8h-1.6V3.8z" />
      <path d="M21.5 27.5l-2 2.1h-8.6l-2.2-2.2V11.6l2.2-2.2h8.6l2 2.1v2.4h5.9V9.3l-4.9-4.8H7.8l-5 4.9v20.2l5 5h14.7l4.9-4.9v-4.6h-5.9z" />
      <path d="M34.6 19.4l1.5-1.5h6.8l1.5 1.5v4.1h5.7v-5.9l-4.4-4.5H33.4l-4.5 4.5v5.9h5.7z" />
      <path d="M44.4 28.3l-1.5 1.5h-6.8l-1.5-1.5v-3.2h-5.7v5l4.5 4.5h12.3l4.4-4.5v-5h-5.7z" />
      <path d="M57.3 22.1l4.1-4.1H65l1.5 1.5v4h5.7v-5.6l-4.8-4.8h-6.9l-3.6 3.6v-3.6h-5.3v10.4h5.7z" />
      <path d="M51.6 25.1h5.7v9.5h-5.7z" />
      <path d="M66.5 25.1h5.7v9.5h-5.7z" />
      <path d="M79.4 19.6 81 18h5.3l2.9 2.6v2.9h5.7V3.8h-5.7v11.5l-2.5-2.2h-8.6l-4.4 4.5v5.9h5.7z" />
      <path d="M89.2 26.2l-3.5 3.5H81l-1.6-1.6v-3h-5.7v5l4.4 4.5h8.1l3.3-3.4v3.4h5.4v-9.5h-5.7z" />
      <path d="M111.2 13.1h5.7v10.4h-5.7z" />
      <path d="M96.4 13.1h5.7v10.4h-5.7z" />
      <path d="M111.2 25.5l-4.2 4.2h-3.4l-1.5-1.4v-3.2h-5.7V30l4.6 4.6h7l3.7-3.7v3.7h5.2v-9.5h-5.7z" />
      <path d="M140.5 13.1h-6.4l-4.5 6.3-4.5-6.3h-6.4l7.7 10.4h6.6z" />
      <path d="M133.8 25.1h-8.4l-6.9 9.5h6.4l4.7-6.6 4.7 6.6h6.5z" />
    </svg>
  );
}
