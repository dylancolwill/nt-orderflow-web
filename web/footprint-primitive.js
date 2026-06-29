// Footprint renderer for Lightweight Charts v4 (series primitive).
// Total x Delta layout: per price row, left = total volume (b+a), right = delta (a-b, green/red).
// Row shaded by volume share; bar POC (max total) outlined. Auto-hides when zoomed out.
(function () {
  'use strict';

  function FootprintPrimitive() {
    var self = this;
    this._chart = null;
    this._series = null;
    this._requestUpdate = null;
    this._fp = null;          // { "<barTs>": { "<price>": {b, a} } }
    this._tickSize = 0.25;
    this._visible = true;
    this._paneView = {
      zOrder: function () { return 'top'; },
      renderer: function () {
        return { draw: function (target) { self._draw(target); } };
      },
    };
  }

  // ── Lightweight Charts primitive lifecycle ─────────────────────────────
  FootprintPrimitive.prototype.attached = function (p) {
    this._chart = p.chart; this._series = p.series; this._requestUpdate = p.requestUpdate;
  };
  FootprintPrimitive.prototype.detached = function () {
    this._chart = null; this._series = null; this._requestUpdate = null;
  };
  FootprintPrimitive.prototype.updateAllViews = function () {};
  FootprintPrimitive.prototype.paneViews = function () { return [this._paneView]; };

  // ── Public API ─────────────────────────────────────────────────────────
  FootprintPrimitive.prototype.setData = function (fp, tickSize) {
    this._fp = fp || null;
    if (tickSize) this._tickSize = tickSize;
    if (this._requestUpdate) this._requestUpdate();
  };
  FootprintPrimitive.prototype.setVisible = function (v) {
    this._visible = !!v;
    if (this._requestUpdate) this._requestUpdate();
  };
  FootprintPrimitive.prototype.isVisible = function () { return this._visible; };

  // ── Rendering ────────────────────────────────────────────────────────────
  FootprintPrimitive.prototype._rowHeightPx = function () {
    if (!this._series || !this._fp) return 0;
    var tsKeys = Object.keys(this._fp);
    if (!tsKeys.length) return 0;
    var rows = this._fp[tsKeys[tsKeys.length - 1]];
    var pk = Object.keys(rows);
    if (!pk.length) return 0;
    var p0 = parseFloat(pk[0]);
    var y0 = this._series.priceToCoordinate(p0);
    var y1 = this._series.priceToCoordinate(p0 + this._tickSize);
    if (y0 === null || y1 === null) return 0;
    return Math.abs(y0 - y1);
  };

  FootprintPrimitive.prototype._draw = function (target) {
    var self = this;
    if (!this._visible || !this._fp || !this._chart || !this._series) return;

    var timeScale = this._chart.timeScale();
    var barSpacing = timeScale.options().barSpacing;
    if (barSpacing < 36) return;                 // too zoomed out — auto-hide

    var rowH = this._rowHeightPx();
    if (!rowH || rowH < 3) return;               // rows too short to read

    target.useBitmapCoordinateSpace(function (scope) {
      var ctx = scope.context;
      var hpr = scope.horizontalPixelRatio;
      var vpr = scope.verticalPixelRatio;

      var fontPx = Math.max(6, Math.min(rowH - 1, 12));
      ctx.font = Math.round(fontPx * vpr) + 'px -apple-system, system-ui, sans-serif';
      ctx.textBaseline = 'middle';

      var cellW = Math.min(barSpacing, 64);      // total width per bar (media px)
      var half = cellW / 2;
      var showText = rowH >= 8;

      Object.keys(self._fp).forEach(function (tsKey) {
        var xc = timeScale.timeToCoordinate(parseInt(tsKey, 10));
        if (xc === null) return;
        var rows = self._fp[tsKey];

        var maxTot = 1, pocPrice = null, pocTot = -1;
        Object.keys(rows).forEach(function (pk) {
          var r = rows[pk], tot = r.b + r.a;
          if (tot > maxTot) maxTot = tot;
          if (tot > pocTot) { pocTot = tot; pocPrice = parseFloat(pk); }
        });

        Object.keys(rows).forEach(function (pk) {
          var price = parseFloat(pk), r = rows[pk];
          var total = r.b + r.a, delta = r.a - r.b;
          var yc = self._series.priceToCoordinate(price);
          if (yc === null) return;

          var x = xc * hpr, y = yc * vpr;
          var hw = half * hpr, rh = rowH * vpr;
          var totShare = total / maxTot;
          var dShare = Math.min(1, Math.abs(delta) / maxTot);

          // left half: total (neutral heatmap)
          ctx.fillStyle = 'rgba(120,140,170,' + (0.10 + 0.45 * totShare).toFixed(3) + ')';
          ctx.fillRect(x - hw, y - rh / 2, hw, rh);
          // right half: delta (green if buyers, red if sellers)
          ctx.fillStyle = (delta >= 0 ? 'rgba(38,166,154,' : 'rgba(239,83,80,') + (0.10 + 0.50 * dShare).toFixed(3) + ')';
          ctx.fillRect(x, y - rh / 2, hw, rh);

          if (price === pocPrice) {
            ctx.strokeStyle = 'rgba(255,235,59,0.9)';
            ctx.lineWidth = Math.max(1, vpr);
            ctx.strokeRect(x - hw, y - rh / 2, hw * 2, rh);
          }

          if (showText) {
            ctx.fillStyle = '#e6e8ec';
            ctx.textAlign = 'right';
            ctx.fillText(String(total), x - 2 * hpr, y);
            ctx.textAlign = 'left';
            ctx.fillStyle = delta > 0 ? '#7ee0d3' : (delta < 0 ? '#f0a0a0' : '#c0c4cc');
            ctx.fillText((delta > 0 ? '+' : '') + delta, x + 2 * hpr, y);
          }
        });
      });
    });
  };

  window.FootprintPrimitive = FootprintPrimitive;
})();
