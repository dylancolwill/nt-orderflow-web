// Per-bar delta table for Lightweight Charts v4 (series primitive).
// Three rows aligned under each bar: Volume, Delta (ask-bid, green/red), CVD (cumulative, cyan/orange).
// Drawn in a strip at the bottom of the pane; auto-hides when bars are too narrow to read.
(function () {
  'use strict';

  function abbr(a) {
    if (a >= 1e6) return (a / 1e6).toFixed(a >= 1e7 ? 0 : 1) + 'M';
    if (a >= 1e3) return (a / 1e3).toFixed(a >= 1e4 ? 0 : 1) + 'K';
    return '' + Math.round(a);
  }
  function fmtVol(n) { return abbr(Math.abs(n)); }
  function fmtSigned(n) { return (n > 0 ? '+' : n < 0 ? '-' : '') + abbr(Math.abs(n)); }

  function TablePrimitive() {
    var self = this;
    this._chart = null; this._series = null; this._req = null;
    this._rows = null; this._visible = false;
    this._bottomFrac = 1;          // fraction of pane height where the table's bottom edge sits
    this._pv = {
      zOrder: function () { return 'top'; },
      renderer: function () { return { draw: function (t) { self._draw(t); } }; },
    };
  }

  TablePrimitive.prototype.attached = function (p) { this._chart = p.chart; this._series = p.series; this._req = p.requestUpdate; };
  TablePrimitive.prototype.detached = function () { this._chart = this._series = this._req = null; };
  TablePrimitive.prototype.updateAllViews = function () {};
  TablePrimitive.prototype.paneViews = function () { return [this._pv]; };

  TablePrimitive.prototype.setData = function (rows) { this._rows = rows || null; if (this._req) this._req(); };
  TablePrimitive.prototype.setBottomFraction = function (f) { this._bottomFrac = f; if (this._req) this._req(); };
  TablePrimitive.prototype.setVisible = function (v) { this._visible = !!v; if (this._req) this._req(); };
  TablePrimitive.prototype.isVisible = function () { return this._visible; };

  TablePrimitive.prototype._draw = function (target) {
    var self = this;
    if (!this._visible || !this._rows || !this._chart || !this._series) return;
    var ts = this._chart.timeScale();
    if (ts.options().barSpacing < 28) return;        // too dense to read

    target.useBitmapCoordinateSpace(function (scope) {
      var ctx = scope.context, hpr = scope.horizontalPixelRatio, vpr = scope.verticalPixelRatio;
      var W = scope.bitmapSize.width, H = scope.bitmapSize.height;
      var rowH = 13 * vpr, pad = 3 * vpr, blockH = rowH * 3 + pad * 2;
      var top = H * self._bottomFrac - blockH;        // sit just above the CVD strip

      ctx.fillStyle = 'rgba(14,15,18,0.72)';          // legibility strip
      ctx.fillRect(0, top, W, blockH);

      ctx.font = Math.round(10 * vpr) + 'px -apple-system, system-ui, sans-serif';
      ctx.textBaseline = 'middle';
      var yV = top + pad + rowH * 0.5, yD = top + pad + rowH * 1.5, yC = top + pad + rowH * 2.5;

      ctx.textAlign = 'center';
      self._rows.forEach(function (r) {
        var xc = ts.timeToCoordinate(r.t);
        if (xc === null) return;
        var x = xc * hpr;
        ctx.fillStyle = '#c8ccd2'; ctx.fillText(fmtVol(r.vol), x, yV);
        if (r.delta != null) { ctx.fillStyle = r.delta > 0 ? '#26a69a' : r.delta < 0 ? '#ef5350' : '#888'; ctx.fillText(fmtSigned(r.delta), x, yD); }
        if (r.cvd   != null) { ctx.fillStyle = r.cvd   > 0 ? '#4dd0e1' : r.cvd   < 0 ? '#ffa726' : '#888'; ctx.fillText(fmtSigned(r.cvd),   x, yC); }
      });

      ctx.textAlign = 'left'; ctx.fillStyle = '#5a5f68';
      ctx.fillText('VOL', 2 * hpr, yV); ctx.fillText('Δ', 2 * hpr, yD); ctx.fillText('CVD', 2 * hpr, yC);
    });
  };

  window.TablePrimitive = TablePrimitive;
})();
