// The block's chart code. It needs PvMiniLib.
function pvDrawChart(containerId, csv) {
  var values = String(csv).split(',').map(Number).filter(function (n) { return !isNaN(n); });
  var el = document.getElementById(containerId);
  var count = PvMini.bars(el, values);
  el.setAttribute('data-bars', String(count));
  return count;
}
