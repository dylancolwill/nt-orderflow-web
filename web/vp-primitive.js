// Daily volume-profile histogram for Lightweight Charts v4 (series primitive).
// Draws horizontal bars from the left edge sized by volume-at-price for the current session.
// POC highlighted; value-area rows brighter than rows outside it. POC/VAH/VAL/VWAP *lines* are
// drawn separately via series.createPriceLine in app.js.
(function () {
  'use strict';

  function VpPrimitive() {
    var self = this;
    this._chart = null; this._series = null; this._req = null;
    this._vp = null; this._tick = 0.25; this._visible = true;
    this._pv = {
      zOrder: function () { return 'bottom'; },               // behind candles/footprint
      renderer: function () { return { draw: function (t) { self._draw(t); } }; },
    };
  }

  VpPrimitive.prototype.attached = function (p) { this._chart = p.chart; this._series = p.series; this._req = p.requestUpdate; };
  VpPrimitive.prototype.detached = function () { this._chart = this._series = this._req = null; };
  VpPrimitive.prototype.updateAllViews = function () {};
  VpPrimitive.prototype.paneViews = function () { return [this._pv]; };

  VpPrimitive.prototype.setData = function (vp, tick) { this._vp = vp || null; if (tick) this._tick = tick; if (this._req) this._req(); };
  VpPrimitive.prototype.setVisible = function (v) { this._visible = !!v; if (this._req) this._req(); };
  VpPrimitive.prototype.isVisible = function () { return this._visible; };

  VpPrimitive.prototype._draw = function (target) {
    var self = this;
    if (!this._visible || !this._vp || !this._vp.rows || !this._series) return;
    var rows = this._vp.rows, prices = Object.keys(rows);
    if (!prices.length) return;

    var maxVol = 1;
    for (var i = 0; i < prices.length; i++) if (rows[prices[i]] > maxVol) maxVol = rows[prices[i]];

    var p0 = parseFloat(prices[0]);
    var y0 = this._series.priceToCoordinate(p0);
    var y1 = this._series.priceToCoordinate(p0 + this._tick);
    if (y0 === null || y1 === null) return;
    var rowH = Math.abs(y0 - y1);
    if (rowH < 0.5) return;

    var poc = this._vp.poc, vah = this._vp.vah, val = this._vp.val;

    target.useBitmapCoordinateSpace(function (scope) {
      var ctx = scope.context, vpr = scope.verticalPixelRatio;
      var right = scope.bitmapSize.width;                     // right edge of the plotting area
      var maxW = right * 0.22;                                // histogram max width (device px)

      prices.forEach(function (pk) {
        var price = parseFloat(pk), vol = rows[pk];
        var yc = self._series.priceToCoordinate(price);
        if (yc === null) return;
        var y = yc * vpr, rh = Math.max(1, rowH * vpr - vpr);
        var w = maxW * (vol / maxVol);
        var inVA = price <= vah && price >= val;
        ctx.fillStyle = (price === poc) ? 'rgba(255,213,79,0.85)'
                      : inVA            ? 'rgba(120,144,200,0.55)'
                                        : 'rgba(120,144,200,0.28)';
        ctx.fillRect(right - w, y - rh / 2, w, rh);           // grow leftward from the right edge
      });
    });
  };

  window.VpPrimitive = VpPrimitive;
})();
