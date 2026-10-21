// A tiny chart library: one bar per value, scaled to the largest value.
var PvMini = (function () {
  function bars(container, values) {
    var max = Math.max.apply(null, values.concat([1]));
    var row = document.createElement('div');
    row.className = 'pv-bars';
    values.forEach(function (v) {
      var bar = document.createElement('div');
      bar.className = 'pv-bar';
      bar.style.height = Math.round(100 * v / max) + '%';
      bar.title = String(v);
      row.appendChild(bar);
    });
    container.appendChild(row);
    return values.length;
  }
  return { bars: bars, version: '1.0' };
})();
