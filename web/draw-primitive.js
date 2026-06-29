// Hand-drawn zones + trendlines for Lightweight Charts v4 (series primitive).
// Zones (rectangles) = filled price bands; trends (sloped lines/rays) = line segments. Horizontal
// levels are drawn separately as native price lines in app.js. Colour/dash/width come from NinjaTrader.
(function () {
  'use strict';

  function dashArr(style, s) {
    return style === 'dashed' ? [6 * s, 4 * s] : style === 'dotted' ? [2 * s, 3 * s] : [];
  }

  function DrawPrimitive() {
    var self = this;
    this._chart = null; this._series = null; this._req = null;
    this._d = null; this._visible = true;
    this._pv = {
      zOrder: function () { return 'bottom'; },
      renderer: function () { return { draw: function (t) { self._draw(t); } }; },
    };
  }

  DrawPrimitive.prototype.attached = function (p) { this._chart = p.chart; this._series = p.series; this._req = p.requestUpdate; };
  DrawPrimitive.prototype.detached = function () { this._chart = this._series = this._req = null; };
  DrawPrimitive.prototype.updateAllViews = function () {};
  DrawPrimitive.prototype.paneViews = function () { return [this._pv]; };

  DrawPrimitive.prototype.setData = function (d) { this._d = d || null; if (this._req) this._req(); };
  DrawPrimitive.prototype.setVisible = function (v) { this._visible = !!v; if (this._req) this._req(); };
  DrawPrimitive.prototype.isVisible = function () { return this._visible; };

  DrawPrimitive.prototype._draw = function (target) {
    var self = this;
    if (!this._visible || !this._d || !this._series || !this._chart) return;
    var ts = this._chart.timeScale();

    target.useBitmapCoordinateSpace(function (scope) {
      var ctx = scope.context, hpr = scope.horizontalPixelRatio, vpr = scope.verticalPixelRatio;
      var W = scope.bitmapSize.width;

      (self._d.zones || []).forEach(function (z) {
        var y1 = self._series.priceToCoordinate(z.p1), y2 = self._series.priceToCoordinate(z.p2);
        if (y1 === null || y2 === null) return;
        ctx.fillStyle = z.color || 'rgba(120,140,200,0.15)';
        ctx.fillRect(0, Math.min(y1, y2) * vpr, W, Math.abs(y2 - y1) * vpr);
      });

      (self._d.trends || []).forEach(function (t) {
        var x1 = ts.timeToCoordinate(t.t1), x2 = ts.timeToCoordinate(t.t2);
        var y1 = self._series.priceToCoordinate(t.p1), y2 = self._series.priceToCoordinate(t.p2);
        if (x1 === null || x2 === null || y1 === null || y2 === null) return;
        ctx.beginPath();
        ctx.strokeStyle = t.color || '#aaa';
        ctx.lineWidth = Math.max(1, (t.width || 1) * vpr);
        ctx.setLineDash(dashArr(t.style, vpr));
        ctx.moveTo(x1 * hpr, y1 * vpr);
        ctx.lineTo(x2 * hpr, y2 * vpr);
        ctx.stroke();
        ctx.setLineDash([]);
      });
    });
  };

  window.DrawPrimitive = DrawPrimitive;
})();
