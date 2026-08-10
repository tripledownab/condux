// The animated Condux wordmark, used as the app's loading indicator: the "condux.ai" logotype floods in
// and out on a loop while the session or a route resolves. Inlined rather than an <img> so the paths
// pick up `currentColor` from the surrounding text color and theme with light/dark; the embedded style
// also honours prefers-reduced-motion (a gentle pulse instead of the flood). Mirrors the shared asset at
// packages/brand/condux-loader.svg.
const STYLES = `
.cx{--t:calc(1.4s * var(--speed, 1));--w:.75}
.cx path{fill:currentColor;stroke:currentColor;stroke-width:var(--w);stroke-linecap:butt;stroke-linejoin:round;stroke-dasharray:1 1;fill-opacity:0}
.cx .g0 path{animation:cxFlood0 var(--t) cubic-bezier(.45,.05,.3,1) infinite}
.cx .g1 path{animation:cxFlood1 var(--t) cubic-bezier(.45,.05,.3,1) infinite}
.cx .g2 path{animation:cxFlood2 var(--t) cubic-bezier(.45,.05,.3,1) infinite}
.cx .g3 path{animation:cxFlood3 var(--t) cubic-bezier(.45,.05,.3,1) infinite}
.cx .g4 path{animation:cxFlood4 var(--t) cubic-bezier(.45,.05,.3,1) infinite}
.cx .g5 path{animation:cxFlood5 var(--t) cubic-bezier(.45,.05,.3,1) infinite}
.cx .g6 path{animation:cxFlood6 var(--t) cubic-bezier(.45,.05,.3,1) infinite}
@keyframes cxFlood0{0%{stroke-dashoffset:1;fill-opacity:0;stroke-opacity:1}46%{stroke-dashoffset:0;fill-opacity:0;stroke-opacity:1}51.5%{stroke-dashoffset:0;fill-opacity:1;stroke-opacity:1}56.5%,86%{stroke-dashoffset:0;fill-opacity:1;stroke-opacity:0}97%,100%{stroke-dashoffset:0;fill-opacity:0;stroke-opacity:0}}
@keyframes cxFlood1{0%{stroke-dashoffset:1;fill-opacity:0;stroke-opacity:1}46%,51.33%{stroke-dashoffset:0;fill-opacity:0;stroke-opacity:1}56.83%{stroke-dashoffset:0;fill-opacity:1;stroke-opacity:1}61.83%,86%{stroke-dashoffset:0;fill-opacity:1;stroke-opacity:0}97%,100%{stroke-dashoffset:0;fill-opacity:0;stroke-opacity:0}}
@keyframes cxFlood2{0%{stroke-dashoffset:1;fill-opacity:0;stroke-opacity:1}46%,56.67%{stroke-dashoffset:0;fill-opacity:0;stroke-opacity:1}62.17%{stroke-dashoffset:0;fill-opacity:1;stroke-opacity:1}67.17%,86%{stroke-dashoffset:0;fill-opacity:1;stroke-opacity:0}97%,100%{stroke-dashoffset:0;fill-opacity:0;stroke-opacity:0}}
@keyframes cxFlood3{0%{stroke-dashoffset:1;fill-opacity:0;stroke-opacity:1}46%,62%{stroke-dashoffset:0;fill-opacity:0;stroke-opacity:1}67.5%{stroke-dashoffset:0;fill-opacity:1;stroke-opacity:1}72.5%,86%{stroke-dashoffset:0;fill-opacity:1;stroke-opacity:0}97%,100%{stroke-dashoffset:0;fill-opacity:0;stroke-opacity:0}}
@keyframes cxFlood4{0%{stroke-dashoffset:1;fill-opacity:0;stroke-opacity:1}46%,67.33%{stroke-dashoffset:0;fill-opacity:0;stroke-opacity:1}72.83%{stroke-dashoffset:0;fill-opacity:1;stroke-opacity:1}77.83%,86%{stroke-dashoffset:0;fill-opacity:1;stroke-opacity:0}97%,100%{stroke-dashoffset:0;fill-opacity:0;stroke-opacity:0}}
@keyframes cxFlood5{0%{stroke-dashoffset:1;fill-opacity:0;stroke-opacity:1}46%,72.67%{stroke-dashoffset:0;fill-opacity:0;stroke-opacity:1}78.17%{stroke-dashoffset:0;fill-opacity:1;stroke-opacity:1}83.17%,86%{stroke-dashoffset:0;fill-opacity:1;stroke-opacity:0}97%,100%{stroke-dashoffset:0;fill-opacity:0;stroke-opacity:0}}
@keyframes cxFlood6{0%{stroke-dashoffset:1;fill-opacity:0;stroke-opacity:1}46%,78%{stroke-dashoffset:0;fill-opacity:0;stroke-opacity:1}83.5%{stroke-dashoffset:0;fill-opacity:1;stroke-opacity:1}86%{stroke-dashoffset:0;fill-opacity:1;stroke-opacity:0}97%,100%{stroke-dashoffset:0;fill-opacity:0;stroke-opacity:0}}
@media (prefers-reduced-motion:reduce){.cx path{animation:none!important;stroke-opacity:0;fill-opacity:1}.cx .mark{animation:cxBreathe 2.6s ease-in-out infinite}@keyframes cxBreathe{0%,100%{opacity:1}50%{opacity:.4}}}
`;

export function Loader({ className }: { className?: string }) {
  return (
    <svg
      className={`cx ${className ?? ""}`}
      viewBox="0 0 144.1 37.9"
      xmlns="http://www.w3.org/2000/svg"
      aria-hidden="true"
    >
      <style>{STYLES}</style>
      <g className="mark">
        <g className="g0 c">
          <path
            pathLength={1}
            d="M21.5 27.5l-2 2.1h-8.6l-2.2-2.2V11.6l2.2-2.2h8.6l2 2.1v2.4h5.9V9.3l-4.9-4.8H7.8l-5 4.9v20.2l5 5h14.7l4.9-4.9v-4.6h-5.9z"
          />
        </g>
        <g className="g1 o">
          <path
            pathLength={1}
            d="M34.6 19.4l1.5-1.5h6.8l1.5 1.5v4.1h5.7v-5.9l-4.4-4.5H33.4l-4.5 4.5v5.9h5.7z"
          />
          <path
            pathLength={1}
            d="M44.4 28.3l-1.5 1.5h-6.8l-1.5-1.5v-3.2h-5.7v5l4.5 4.5h12.3l4.4-4.5v-5h-5.7z"
          />
        </g>
        <g className="g2 n">
          <path
            pathLength={1}
            d="M57.3 22.1l4.1-4.1H65l1.5 1.5v4h5.7v-5.6l-4.8-4.8h-6.9l-3.6 3.6v-3.6h-5.3v10.4h5.7z"
          />
          <path pathLength={1} d="M51.6 25.1h5.7v9.5h-5.7z" />
          <path pathLength={1} d="M66.5 25.1h5.7v9.5h-5.7z" />
        </g>
        <g className="g3 d">
          <path
            pathLength={1}
            d="M79.4 19.6 81 18h5.3l2.9 2.6v2.9h5.7V3.8h-5.7v11.5l-2.5-2.2h-8.6l-4.4 4.5v5.9h5.7z"
          />
          <path
            pathLength={1}
            d="M89.2 26.2l-3.5 3.5H81l-1.6-1.6v-3h-5.7v5l4.4 4.5h8.1l3.3-3.4v3.4h5.4v-9.5h-5.7z"
          />
        </g>
        <g className="g4 u">
          <path pathLength={1} d="M111.2 13.1h5.7v10.4h-5.7z" />
          <path pathLength={1} d="M96.4 13.1h5.7v10.4h-5.7z" />
          <path
            pathLength={1}
            d="M111.2 25.5l-4.2 4.2h-3.4l-1.5-1.4v-3.2h-5.7V30l4.6 4.6h7l3.7-3.7v3.7h5.2v-9.5h-5.7z"
          />
        </g>
        <g className="g5 x">
          <path pathLength={1} d="M140.5 13.1h-6.4l-4.5 6.3-4.5-6.3h-6.4l7.7 10.4h6.6z" />
          <path pathLength={1} d="M133.8 25.1h-8.4l-6.9 9.5h6.4l4.7-6.6 4.7 6.6h6.5z" />
        </g>
        <g className="g6 ai">
          <path
            pathLength={1}
            d="M133.4 3.8h1.5l3 7.8h-1.6l-.7-1.7h-2.9l-.7 1.7h-1.6l3-7.8zm1.9 4.8-1.1-2.9-1.1 2.9h2.2z"
          />
          <path pathLength={1} d="M138.7 3.8h1.6v7.8h-1.6V3.8z" />
        </g>
      </g>
    </svg>
  );
}
